using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using OneCode.App.Query;
using OneCode.App.Services;
using OneCode.App.Services.PlanMode;
using OneCode.App.Session;
using OneCode.App.Tui;
using OneCode.Core.Build;
using OneCode.Core.Domain;
using OneCode.Core.PlanMode;

namespace OneCode.Tests;

public sealed class PlanWorkflowApplicationServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "OneCodePlanWorkflowTests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task SubmitAsync_NewWorkflow_PersistsFinalizingStateAndRevision()
    {
        var sut = CreateSut();
        var sessionId = SessionId.NewId();

        var result = await sut.SubmitAsync(
            CreateSubmitCommand(sessionId, "plan-run-1"),
            TestContext.Current.CancellationToken);

        result.Workflow.State.Should().Be(PlanWorkflowState.FinalizingPlanRun);
        result.Workflow.Version.Should().Be(1);
        result.Workflow.SubmittedRevision.Should().Be(1);
        result.Workflow.ActiveRunId.Should().Be("plan-run-1");
        result.Revision.Status.Should().Be(PlanRevisionStatus.Submitted);

        var restored = await sut.GetAsync(sessionId, TestContext.Current.CancellationToken);
        restored.Should().BeEquivalentTo(result.Workflow);
    }

    [Fact]
    public async Task PlanRunCompleted_TransitionsToAwaitingApproval()
    {
        var sut = CreateSut();
        var sessionId = SessionId.NewId();
        var submitted = await sut.SubmitAsync(
            CreateSubmitCommand(sessionId, "plan-run-1"),
            TestContext.Current.CancellationToken);

        await sut.HandleRunEventAsync(
            new PlanRunCompletedEvent(
                sessionId,
                submitted.Workflow.Id,
                "plan-run-1",
                ProtocolValid: true,
                DateTimeOffset.UtcNow),
            TestContext.Current.CancellationToken);

        var restored = await sut.GetAsync(sessionId, TestContext.Current.CancellationToken);
        restored!.State.Should().Be(PlanWorkflowState.AwaitingApproval);
        restored.ActiveRunId.Should().BeNull();
        restored.Version.Should().Be(2);
    }

    [Fact]
    public async Task ApproveAsync_FreezesSnapshotAndBuildRunStartIsAccepted()
    {
        var sut = CreateSut();
        var sessionId = SessionId.NewId();
        var submitted = await sut.SubmitAsync(
            CreateSubmitCommand(sessionId, "plan-run-1"),
            TestContext.Current.CancellationToken);
        await sut.HandleRunEventAsync(
            new PlanRunCompletedEvent(
                sessionId,
                submitted.Workflow.Id,
                "plan-run-1",
                ProtocolValid: true,
                DateTimeOffset.UtcNow),
            TestContext.Current.CancellationToken);
        var awaiting = (await sut.GetAsync(sessionId, TestContext.Current.CancellationToken))!;

        var approved = await sut.ApproveAsync(
            new ApprovePlanCommand(
                "approve-1",
                sessionId,
                awaiting.Id,
                awaiting.SubmittedRevision!.Value,
                awaiting.Version,
                "user"),
            TestContext.Current.CancellationToken);

        approved.Workflow.State.Should().Be(PlanWorkflowState.StartingExecution);
        approved.Workflow.ApprovedSnapshot.Should().NotBeNull();
        approved.Workflow.StepExecutions.Should().ContainSingle()
            .Which.Status.Should().Be(PlanStepExecutionStatus.Pending);

        await sut.HandleRunEventAsync(
            new BuildRunStartedEvent(
                sessionId,
                awaiting.Id,
                "build-run-1",
                DateTimeOffset.UtcNow),
            TestContext.Current.CancellationToken);

        var executing = await sut.GetAsync(sessionId, TestContext.Current.CancellationToken);
        executing!.State.Should().Be(PlanWorkflowState.Executing);
        executing.ActiveRunId.Should().Be("build-run-1");
        executing.ActiveRunKind.Should().Be(PlanRunKind.Build);
    }

    [Fact]
    public async Task ApproveAsync_ReplayedCommand_IsIdempotent()
    {
        var sut = CreateSut();
        var sessionId = SessionId.NewId();
        var submitted = await sut.SubmitAsync(
            CreateSubmitCommand(sessionId, "plan-run-1"),
            TestContext.Current.CancellationToken);
        await sut.HandleRunEventAsync(
            new PlanRunCompletedEvent(
                sessionId,
                submitted.Workflow.Id,
                "plan-run-1",
                ProtocolValid: true,
                DateTimeOffset.UtcNow),
            TestContext.Current.CancellationToken);
        var awaiting = (await sut.GetAsync(sessionId, TestContext.Current.CancellationToken))!;
        var command = new ApprovePlanCommand(
            "approve-1",
            sessionId,
            awaiting.Id,
            awaiting.SubmittedRevision!.Value,
            awaiting.Version,
            "user");

        var first = await sut.ApproveAsync(command, TestContext.Current.CancellationToken);
        var replay = await sut.ApproveAsync(command, TestContext.Current.CancellationToken);

        replay.IsDuplicateCommand.Should().BeTrue();
        replay.Workflow.Version.Should().Be(first.Workflow.Version);
    }

    [Fact]
    public async Task CancelAsync_FromFinalizingPlanRun_PersistsCancelledState()
    {
        var sut = CreateSut();
        var sessionId = SessionId.NewId();
        var submitted = await sut.SubmitAsync(
            CreateSubmitCommand(sessionId, "plan-run-1"),
            TestContext.Current.CancellationToken);

        var cancelled = await sut.CancelAsync(
            new CancelPlanCommand(
                "cancel-1",
                sessionId,
                submitted.Workflow.Id,
                submitted.Workflow.Version,
                "User cancelled."),
            TestContext.Current.CancellationToken);

        cancelled.Workflow.State.Should().Be(PlanWorkflowState.Cancelled);
        cancelled.Workflow.ActiveRunId.Should().BeNull();
        cancelled.Workflow.LastErrorCode.Should().Be("Cancelled");
        (await sut.GetAsync(sessionId, TestContext.Current.CancellationToken))!
            .State.Should().Be(PlanWorkflowState.Cancelled);
    }

    [Fact]
    public async Task RegisterStartAttemptAsync_PersistsAttemptAndExponentialRetry()
    {
        var sut = CreateSut();
        var sessionId = SessionId.NewId();
        var submitted = await sut.SubmitAsync(
            CreateSubmitCommand(sessionId, "plan-run-1"),
            TestContext.Current.CancellationToken);
        await sut.HandleRunEventAsync(
            new PlanRunCompletedEvent(
                sessionId,
                submitted.Workflow.Id,
                "plan-run-1",
                ProtocolValid: true,
                DateTimeOffset.UtcNow),
            TestContext.Current.CancellationToken);
        var awaiting = (await sut.GetAsync(sessionId, TestContext.Current.CancellationToken))!;
        var approved = await sut.ApproveAsync(
            new ApprovePlanCommand(
                "approve-1",
                sessionId,
                awaiting.Id,
                awaiting.SubmittedRevision!.Value,
                awaiting.Version,
                "user"),
            TestContext.Current.CancellationToken);
        var attemptedAt = DateTimeOffset.UtcNow;

        var attempt = await sut.RegisterStartAttemptAsync(
            new RegisterPlanStartAttemptCommand(
                "start-1",
                sessionId,
                awaiting.Id,
                approved.Workflow.Version,
                attemptedAt),
            TestContext.Current.CancellationToken);

        attempt.Workflow.State.Should().Be(PlanWorkflowState.StartingExecution);
        attempt.Workflow.StartAttempt.Should().Be(1);
        attempt.Workflow.NextRetryAt.Should().Be(attemptedAt.AddSeconds(1));
    }

    [Fact]
    public async Task HandleRunEventAsync_StartRetryExhausted_FromStartingExecution_PersistsFailedState()
    {
        var sut = CreateSut();
        var sessionId = SessionId.NewId();
        var submitted = await sut.SubmitAsync(
            CreateSubmitCommand(sessionId, "plan-run-1"),
            TestContext.Current.CancellationToken);
        await sut.HandleRunEventAsync(
            new PlanRunCompletedEvent(
                sessionId,
                submitted.Workflow.Id,
                "plan-run-1",
                ProtocolValid: true,
                DateTimeOffset.UtcNow),
            TestContext.Current.CancellationToken);
        var awaiting = (await sut.GetAsync(sessionId, TestContext.Current.CancellationToken))!;
        var approved = await sut.ApproveAsync(
            new ApprovePlanCommand(
                "approve-1",
                sessionId,
                awaiting.Id,
                awaiting.SubmittedRevision!.Value,
                awaiting.Version,
                "user"),
            TestContext.Current.CancellationToken);
        var expectedRunId = $"build-{approved.Workflow.ExecutionRequestId}";

        await sut.HandleRunEventAsync(
            new BuildRunFailedEvent(
                sessionId,
                approved.Workflow.Id,
                expectedRunId,
                "StartRetryExhausted",
                "Execution start exhausted.",
                DateTimeOffset.UtcNow),
            TestContext.Current.CancellationToken);

        var failed = await sut.GetAsync(sessionId, TestContext.Current.CancellationToken);
        failed!.State.Should().Be(PlanWorkflowState.Failed);
        failed.LastErrorCode.Should().Be("StartRetryExhausted");
        failed.ActiveRunId.Should().BeNull();
    }

    [Fact]
    public async Task CancelAsync_FromStartingExecution_CancelsPendingSteps()
    {
        var sut = CreateSut();
        var sessionId = SessionId.NewId();
        var submitted = await sut.SubmitAsync(
            CreateSubmitCommand(sessionId, "plan-run-1"),
            TestContext.Current.CancellationToken);
        await sut.HandleRunEventAsync(
            new PlanRunCompletedEvent(
                sessionId,
                submitted.Workflow.Id,
                "plan-run-1",
                ProtocolValid: true,
                DateTimeOffset.UtcNow),
            TestContext.Current.CancellationToken);
        var awaiting = (await sut.GetAsync(sessionId, TestContext.Current.CancellationToken))!;
        var approved = await sut.ApproveAsync(
            new ApprovePlanCommand(
                "approve-1",
                sessionId,
                awaiting.Id,
                awaiting.SubmittedRevision!.Value,
                awaiting.Version,
                "user"),
            TestContext.Current.CancellationToken);

        var cancelled = await sut.CancelAsync(
            new CancelPlanCommand(
                "cancel-1",
                sessionId,
                awaiting.Id,
                approved.Workflow.Version,
                "Cancelled before execution."),
            TestContext.Current.CancellationToken);

        cancelled.Workflow.State.Should().Be(PlanWorkflowState.Cancelled);
        cancelled.Workflow.StepExecutions.Should().OnlyContain(step =>
            step.Status == PlanStepExecutionStatus.Cancelled);
        cancelled.Workflow.NextRetryAt.Should().BeNull();
    }

    [Fact]
    public async Task Store_LoadRecoverableExecutionAsync_ReturnsAllNonTerminalExecutionWorkflows()
    {
        var sut = CreateSut();
        var startingSession = SessionId.NewId();
        var startingSubmit = await sut.SubmitAsync(
            CreateSubmitCommand(startingSession, "plan-run-starting"),
            TestContext.Current.CancellationToken);
        await sut.HandleRunEventAsync(new PlanRunCompletedEvent(
            startingSession,
            startingSubmit.Workflow.Id,
            "plan-run-starting",
            ProtocolValid: true,
            DateTimeOffset.UtcNow), TestContext.Current.CancellationToken);
        var awaiting = (await sut.GetAsync(startingSession, TestContext.Current.CancellationToken))!;
        var starting = await sut.ApproveAsync(new ApprovePlanCommand(
            "approve-starting",
            startingSession,
            awaiting.Id,
            awaiting.SubmittedRevision!.Value,
            awaiting.Version,
            "user"), TestContext.Current.CancellationToken);

        var executingSession = SessionId.NewId();
        var executing = CreateExecutingWorkflow(executingSession, PlanWorkflowState.Executing);
        var verifyingSession = SessionId.NewId();
        var verifying = CreateExecutingWorkflow(verifyingSession, PlanWorkflowState.Verifying);
        var store = new PlanAggregateStore(_root);
        await store.SaveAsync(
            new PlanAggregate(executing, [CreateRevision(executing)]),
            expectedVersion: -1,
            TestContext.Current.CancellationToken);
        await store.SaveAsync(
            new PlanAggregate(verifying, [CreateRevision(verifying)]),
            expectedVersion: -1,
            TestContext.Current.CancellationToken);

        var finalizingSession = SessionId.NewId();
        await sut.SubmitAsync(
            CreateSubmitCommand(finalizingSession, "plan-run-finalizing"),
            TestContext.Current.CancellationToken);

        var recoverable = await store.LoadRecoverableExecutionAsync(TestContext.Current.CancellationToken);

        recoverable.Should().HaveCount(3);
        recoverable.Should().ContainEquivalentOf(starting.Workflow);
        recoverable.Should().ContainEquivalentOf(executing);
        recoverable.Should().ContainEquivalentOf(verifying);
    }

    [Fact]
    public async Task Store_LoadRecoverableExecutionAsync_SkipsCorruptWorkflowAndReturnsValidOnes()
    {
        var valid = CreateExecutingWorkflow(SessionId.NewId(), PlanWorkflowState.Executing);
        var store = new PlanAggregateStore(_root);
        await store.SaveAsync(new PlanAggregate(valid, [CreateRevision(valid)]), expectedVersion: -1, TestContext.Current.CancellationToken);
        var corruptPath = Path.Combine(
            _root,
            SessionId.NewId().ToString(),
            PlanWorkflowId.NewId().ToString(),
            "aggregate.json");
        Directory.CreateDirectory(Path.GetDirectoryName(corruptPath)!);
        await File.WriteAllTextAsync(corruptPath, "{ corrupt", TestContext.Current.CancellationToken);

        var recoverable = await store.LoadRecoverableExecutionAsync(TestContext.Current.CancellationToken);

        recoverable.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(valid);
    }

    [Fact]
    public async Task RecoveryService_ScanDueAsync_DispatchesOnlyDueLoadedWorkflows()
    {
        var dueSessionId = SessionId.NewId();
        var futureSessionId = SessionId.NewId();
        var unloadedSessionId = SessionId.NewId();
        var now = DateTimeOffset.UtcNow;
        var due = CreateStartingWorkflow(dueSessionId, now.AddSeconds(-1));
        var future = CreateStartingWorkflow(futureSessionId, now.AddMinutes(1));
        var unloaded = CreateStartingWorkflow(unloadedSessionId, now.AddSeconds(-1));
        var store = Substitute.For<IPlanAggregateStore>();
        store.LoadRecoverableExecutionAsync(Arg.Any<CancellationToken>())
            .Returns([due, future, unloaded]);
        var workflowService = Substitute.For<IPlanWorkflowApplicationService>();
        var buildRunStore = Substitute.For<IBuildRunStore>();
        var dispatcher = Substitute.For<IPlanAgentRunDispatcher>();
        var sessionManager = Substitute.For<ISessionManager>();
        sessionManager.WorkingDirectory.Returns(_root);
        sessionManager.GetConversation(dueSessionId).Returns(CreateConversation(dueSessionId));
        sessionManager.GetConversation(futureSessionId).Returns(CreateConversation(futureSessionId));
        sessionManager.GetConversation(unloadedSessionId).Returns((Conversation?)null);
        var session = CreateInteractiveSession(sessionManager);
        var sut = new PlanExecutionRecoveryService(
            store,
            workflowService,
            buildRunStore,
            dispatcher,
            NullLogger<PlanExecutionRecoveryService>.Instance);

        await sut.ScanDueAsync(session, now, TestContext.Current.CancellationToken);

        await dispatcher.Received(1).StartBuildAsync(
            session,
            due,
            TestContext.Current.CancellationToken);
        await dispatcher.DidNotReceive().StartBuildAsync(
            session,
            future,
            Arg.Any<CancellationToken>());
        await dispatcher.DidNotReceive().StartBuildAsync(
            session,
            unloaded,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RecoveryService_ExecutingWorkflow_ResumesMatchingNonTerminalBuildRun()
    {
        var sessionId = SessionId.NewId();
        var workflow = CreateExecutingWorkflow(sessionId, PlanWorkflowState.Executing);
        var buildRun = CreateMatchingBuildRun(workflow, BuildRunState.Implementing);
        var store = Substitute.For<IPlanAggregateStore>();
        store.LoadRecoverableExecutionAsync(Arg.Any<CancellationToken>()).Returns([workflow]);
        var workflowService = Substitute.For<IPlanWorkflowApplicationService>();
        var buildRunStore = Substitute.For<IBuildRunStore>();
        buildRunStore.LoadAsync(sessionId, Arg.Any<CancellationToken>()).Returns(buildRun);
        var bound = workflow with { BuildRunId = buildRun.Id.ToString(), Version = workflow.Version + 1 };
        workflowService.BindBuildRunAsync(
                Arg.Any<BindPlanBuildRunCommand>(),
                Arg.Any<CancellationToken>())
            .Returns(new PlanTransitionResult(bound));
        var dispatcher = Substitute.For<IPlanAgentRunDispatcher>();
        var sessionManager = Substitute.For<ISessionManager>();
        sessionManager.WorkingDirectory.Returns(_root);
        sessionManager.GetConversation(sessionId).Returns(CreateConversation(sessionId));
        var session = CreateInteractiveSession(sessionManager);
        var sut = new PlanExecutionRecoveryService(
            store,
            workflowService,
            buildRunStore,
            dispatcher,
            NullLogger<PlanExecutionRecoveryService>.Instance);

        await sut.ScanDueAsync(session, DateTimeOffset.UtcNow, TestContext.Current.CancellationToken);

        await workflowService.Received(1).BindBuildRunAsync(
            Arg.Is<BindPlanBuildRunCommand>(command => command.BuildRunId == buildRun.Id.ToString()),
            TestContext.Current.CancellationToken);
        await dispatcher.Received(1).ResumeBuildAsync(
            session,
            bound,
            TestContext.Current.CancellationToken);
        await workflowService.DidNotReceive().FailExecutionRecoveryAsync(
            Arg.Any<FailPlanExecutionRecoveryCommand>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RecoveryService_CompletedBuildRunWithoutPlanVerification_FailsClosed()
    {
        var sessionId = SessionId.NewId();
        var workflow = CreateExecutingWorkflow(sessionId, PlanWorkflowState.Verifying);
        var buildRun = CreateMatchingBuildRun(workflow, BuildRunState.Completed);
        workflow = workflow with { BuildRunId = buildRun.Id.ToString() };
        var store = Substitute.For<IPlanAggregateStore>();
        store.LoadRecoverableExecutionAsync(Arg.Any<CancellationToken>()).Returns([workflow]);
        var workflowService = Substitute.For<IPlanWorkflowApplicationService>();
        var buildRunStore = Substitute.For<IBuildRunStore>();
        buildRunStore.LoadByIdAsync(buildRun.Id, Arg.Any<CancellationToken>()).Returns(buildRun);
        var dispatcher = Substitute.For<IPlanAgentRunDispatcher>();
        var sessionManager = Substitute.For<ISessionManager>();
        sessionManager.WorkingDirectory.Returns(_root);
        sessionManager.GetConversation(sessionId).Returns(CreateConversation(sessionId));
        var session = CreateInteractiveSession(sessionManager);
        var sut = new PlanExecutionRecoveryService(
            store,
            workflowService,
            buildRunStore,
            dispatcher,
            NullLogger<PlanExecutionRecoveryService>.Instance);

        await sut.ScanDueAsync(session, DateTimeOffset.UtcNow, TestContext.Current.CancellationToken);

        await workflowService.Received(1).FailExecutionRecoveryAsync(
            Arg.Is<FailPlanExecutionRecoveryCommand>(command =>
                command.ErrorCode == "PlanVerificationProtocolMissing"
                && command.ExpectedWorkflowVersion == workflow.Version),
            TestContext.Current.CancellationToken);
        await dispatcher.DidNotReceive().ResumeBuildAsync(
            Arg.Any<InteractiveSession>(),
            Arg.Any<PlanWorkflow>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task BindBuildRunAsync_BindsOnceAndRejectsDifferentRun()
    {
        var sessionId = SessionId.NewId();
        var sut = CreateSut();
        var executing = await CreatePersistedExecutingWorkflowAsync(sut, sessionId);
        var firstRunId = BuildRunId.New().ToString();
        var command = new BindPlanBuildRunCommand(
            $"bind-{firstRunId}",
            sessionId,
            executing.Id,
            executing.ActiveRunId!,
            firstRunId);

        var first = await sut.BindBuildRunAsync(command, TestContext.Current.CancellationToken);
        var replay = await sut.BindBuildRunAsync(command, TestContext.Current.CancellationToken);
        var conflicting = () => sut.BindBuildRunAsync(new BindPlanBuildRunCommand(
            "bind-conflict",
            sessionId,
            executing.Id,
            executing.ActiveRunId!,
            BuildRunId.New().ToString()), TestContext.Current.CancellationToken);

        first.Workflow.BuildRunId.Should().Be(firstRunId);
        replay.IsDuplicateCommand.Should().BeTrue();
        await conflicting.Should().ThrowAsync<PlanTransitionException>();
    }

    [Fact]
    public async Task FailExecutionRecoveryAsync_ReplayedCommand_IsIdempotent()
    {
        var sessionId = SessionId.NewId();
        var sut = CreateSut();
        var executing = await CreatePersistedExecutingWorkflowAsync(sut, sessionId);
        var command = new FailPlanExecutionRecoveryCommand(
            $"recover-fail-{executing.Id}-{executing.Version}",
            sessionId,
            executing.Id,
            executing.Version,
            "BuildRunFailed",
            "persisted run failed",
            DateTimeOffset.UtcNow);

        var first = await sut.FailExecutionRecoveryAsync(
            command,
            TestContext.Current.CancellationToken);
        var replay = await sut.FailExecutionRecoveryAsync(
            command,
            TestContext.Current.CancellationToken);

        first.Workflow.State.Should().Be(PlanWorkflowState.Failed);
        first.Workflow.LastErrorCode.Should().Be("BuildRunFailed");
        replay.IsDuplicateCommand.Should().BeTrue();
        replay.Workflow.Version.Should().Be(first.Workflow.Version);
    }

    [Fact]
    public async Task RecoveryService_CancelledBuildRun_CancelsPlanWorkflow()
    {
        var sessionId = SessionId.NewId();
        var workflow = CreateExecutingWorkflow(sessionId, PlanWorkflowState.Executing);
        var buildRun = CreateMatchingBuildRun(workflow, BuildRunState.Cancelled) with
        {
            FailureSummary = "cancelled during shutdown",
        };
        workflow = workflow with { BuildRunId = buildRun.Id.ToString() };
        var store = Substitute.For<IPlanAggregateStore>();
        store.LoadRecoverableExecutionAsync(Arg.Any<CancellationToken>()).Returns([workflow]);
        var workflowService = Substitute.For<IPlanWorkflowApplicationService>();
        var buildRunStore = Substitute.For<IBuildRunStore>();
        buildRunStore.LoadByIdAsync(buildRun.Id, Arg.Any<CancellationToken>()).Returns(buildRun);
        var dispatcher = Substitute.For<IPlanAgentRunDispatcher>();
        var sessionManager = Substitute.For<ISessionManager>();
        sessionManager.WorkingDirectory.Returns(_root);
        sessionManager.GetConversation(sessionId).Returns(CreateConversation(sessionId));
        var session = CreateInteractiveSession(sessionManager);
        var sut = new PlanExecutionRecoveryService(
            store,
            workflowService,
            buildRunStore,
            dispatcher,
            NullLogger<PlanExecutionRecoveryService>.Instance);

        await sut.ScanDueAsync(session, DateTimeOffset.UtcNow, TestContext.Current.CancellationToken);

        await workflowService.Received(1).CancelAsync(
            Arg.Is<CancelPlanCommand>(command =>
                command.SessionId == sessionId
                && command.PlanId == workflow.Id
                && command.ExpectedWorkflowVersion == workflow.Version
                && command.Reason == "cancelled during shutdown"),
            TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task RecoveryService_MismatchedPlan_FailsWorkflowAndContinuesScanning()
    {
        var firstSession = SessionId.NewId();
        var secondSession = SessionId.NewId();
        var mismatched = CreateExecutingWorkflow(firstSession, PlanWorkflowState.Executing);
        var recoverable = CreateExecutingWorkflow(secondSession, PlanWorkflowState.Executing);
        var store = Substitute.For<IPlanAggregateStore>();
        store.LoadRecoverableExecutionAsync(Arg.Any<CancellationToken>())
            .Returns([mismatched, recoverable]);
        var workflowService = Substitute.For<IPlanWorkflowApplicationService>();
        var buildRunStore = Substitute.For<IBuildRunStore>();
        buildRunStore.LoadAsync(firstSession, Arg.Any<CancellationToken>())
            .Returns(CreateMatchingBuildRun(mismatched, BuildRunState.Implementing) with
            {
                Plan = new BuildPlan("wrong", [], [], [], [], true),
            });
        var recoverableRun = CreateMatchingBuildRun(recoverable, BuildRunState.Implementing);
        buildRunStore.LoadAsync(secondSession, Arg.Any<CancellationToken>())
            .Returns(recoverableRun);
        var boundRecoverable = recoverable with
        {
            BuildRunId = recoverableRun.Id.ToString(),
            Version = recoverable.Version + 1,
        };
        workflowService.BindBuildRunAsync(
                Arg.Is<BindPlanBuildRunCommand>(command => command.SessionId == secondSession),
                Arg.Any<CancellationToken>())
            .Returns(new PlanTransitionResult(boundRecoverable));
        var dispatcher = Substitute.For<IPlanAgentRunDispatcher>();
        var sessionManager = Substitute.For<ISessionManager>();
        sessionManager.WorkingDirectory.Returns(_root);
        sessionManager.GetConversation(firstSession).Returns(CreateConversation(firstSession));
        sessionManager.GetConversation(secondSession).Returns(CreateConversation(secondSession));
        var session = CreateInteractiveSession(sessionManager);
        var sut = new PlanExecutionRecoveryService(
            store,
            workflowService,
            buildRunStore,
            dispatcher,
            NullLogger<PlanExecutionRecoveryService>.Instance);

        await sut.ScanDueAsync(session, DateTimeOffset.UtcNow, TestContext.Current.CancellationToken);

        await workflowService.Received(1).FailExecutionRecoveryAsync(
            Arg.Is<FailPlanExecutionRecoveryCommand>(command =>
                command.SessionId == firstSession
                && command.ErrorCode == "BuildRunPlanMismatch"),
            TestContext.Current.CancellationToken);
        await dispatcher.Received(1).ResumeBuildAsync(
            session,
            boundRecoverable,
            TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task RecoveryService_ScanDueAsync_SuppressesConcurrentScan()
    {
        var sessionId = SessionId.NewId();
        var now = DateTimeOffset.UtcNow;
        var workflow = CreateStartingWorkflow(sessionId, now.AddSeconds(-1));
        var store = Substitute.For<IPlanAggregateStore>();
        var releaseLoad = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        store.LoadRecoverableExecutionAsync(Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                await releaseLoad.Task;
                return [workflow];
            });
        var workflowService = Substitute.For<IPlanWorkflowApplicationService>();
        var buildRunStore = Substitute.For<IBuildRunStore>();
        var dispatcher = Substitute.For<IPlanAgentRunDispatcher>();
        var sessionManager = Substitute.For<ISessionManager>();
        sessionManager.WorkingDirectory.Returns(_root);
        sessionManager.GetConversation(sessionId).Returns(CreateConversation(sessionId));
        var session = CreateInteractiveSession(sessionManager);
        var sut = new PlanExecutionRecoveryService(
            store,
            workflowService,
            buildRunStore,
            dispatcher,
            NullLogger<PlanExecutionRecoveryService>.Instance);

        var first = sut.ScanDueAsync(session, now, TestContext.Current.CancellationToken);
        var second = sut.ScanDueAsync(session, now, TestContext.Current.CancellationToken);
        releaseLoad.SetResult();
        await Task.WhenAll(first, second);

        await store.Received(1).LoadRecoverableExecutionAsync(Arg.Any<CancellationToken>());
        await dispatcher.Received(1).StartBuildAsync(
            session,
            workflow,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Dispatcher_FutureRetry_DoesNotRegisterOrStartBuildRun()
    {
        var sessionId = SessionId.NewId();
        var workflow = CreateStartingWorkflow(sessionId, DateTimeOffset.UtcNow.AddMinutes(1));
        var workflowService = Substitute.For<IPlanWorkflowApplicationService>();
        workflowService.GetAsync(sessionId, Arg.Any<CancellationToken>()).Returns(workflow);
        var runner = Substitute.For<IConversationRunner>();
        var session = CreateInteractiveSession(Substitute.For<ISessionManager>(), runner);
        var sut = CreateDispatcher(workflowService);

        await sut.StartBuildAsync(session, workflow, TestContext.Current.CancellationToken);

        await workflowService.DidNotReceive().RegisterStartAttemptAsync(
            Arg.Any<RegisterPlanStartAttemptCommand>(),
            Arg.Any<CancellationToken>());
        await workflowService.DidNotReceive().HandleRunEventAsync(
            Arg.Any<BuildRunStartedEvent>(),
            Arg.Any<CancellationToken>());
        runner.DidNotReceive().StreamWorkflowRunAsync(
            Arg.Any<WorkflowRunRequest>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Dispatcher_StartBuild_EmitsStartAndBindsObservedBuildRun()
    {
        var sessionId = SessionId.NewId();
        var workflow = CreateStartingWorkflow(sessionId, nextRetryAt: null);
        var attempted = workflow with
        {
            StartAttempt = 1,
            Version = workflow.Version + 1,
            NextRetryAt = DateTimeOffset.UtcNow.AddSeconds(1),
        };
        var executing = attempted with
        {
            State = PlanWorkflowState.Executing,
            ActiveRunId = $"build-{workflow.ExecutionRequestId}",
        };
        var bound = executing with { BuildRunId = "br-observed", Version = executing.Version + 1 };
        var workflowService = Substitute.For<IPlanWorkflowApplicationService>();
        workflowService.GetAsync(sessionId, Arg.Any<CancellationToken>())
            .Returns(workflow, executing, bound, bound);
        workflowService.RegisterStartAttemptAsync(
                Arg.Any<RegisterPlanStartAttemptCommand>(),
                Arg.Any<CancellationToken>())
            .Returns(new PlanTransitionResult(attempted));
        workflowService.BindBuildRunAsync(
                Arg.Any<BindPlanBuildRunCommand>(),
                Arg.Any<CancellationToken>())
            .Returns(new PlanTransitionResult(bound));
        var runner = Substitute.For<IConversationRunner>();
        runner.StreamWorkflowRunAsync(Arg.Any<WorkflowRunRequest>(), Arg.Any<CancellationToken>())
            .Returns(SingleQueryEvent(new BuildRunStateEvent(
                new BuildRunId("br-observed"),
                BuildRunState.Implementing,
                1,
                [])));
        var sessionManager = Substitute.For<ISessionManager>();
        sessionManager.WorkingDirectory.Returns(_root);
        sessionManager.GetConversation(sessionId).Returns(CreateConversation(sessionId));
        var session = CreateInteractiveSession(sessionManager, runner);
        var sut = CreateDispatcher(workflowService);

        await sut.StartBuildAsync(session, workflow, TestContext.Current.CancellationToken);

        await workflowService.Received(1).HandleRunEventAsync(
            Arg.Is<BuildRunStartedEvent>(evt => evt.RunId == $"build-{workflow.ExecutionRequestId}"),
            TestContext.Current.CancellationToken);
        await workflowService.Received(1).BindBuildRunAsync(
            Arg.Is<BindPlanBuildRunCommand>(command =>
                command.RunId == $"build-{workflow.ExecutionRequestId}"
                && command.BuildRunId == "br-observed"),
            TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Dispatcher_ResumeBuild_DoesNotEmitSecondBuildRunStartedEvent()
    {
        var sessionId = SessionId.NewId();
        var workflow = CreateExecutingWorkflow(sessionId, PlanWorkflowState.Executing);
        var workflowService = Substitute.For<IPlanWorkflowApplicationService>();
        workflowService.GetAsync(sessionId, Arg.Any<CancellationToken>()).Returns(workflow, workflow);
        var runner = Substitute.For<IConversationRunner>();
        runner.StreamWorkflowRunAsync(Arg.Any<WorkflowRunRequest>(), Arg.Any<CancellationToken>())
            .Returns(EmptyQueryEvents());
        var sessionManager = Substitute.For<ISessionManager>();
        sessionManager.WorkingDirectory.Returns(_root);
        sessionManager.GetConversation(sessionId).Returns(CreateConversation(sessionId));
        var session = CreateInteractiveSession(sessionManager, runner);
        var sut = CreateDispatcher(workflowService);

        await sut.ResumeBuildAsync(session, workflow, TestContext.Current.CancellationToken);

        await workflowService.DidNotReceive().RegisterStartAttemptAsync(
            Arg.Any<RegisterPlanStartAttemptCommand>(),
            Arg.Any<CancellationToken>());
        await workflowService.DidNotReceive().HandleRunEventAsync(
            Arg.Any<BuildRunStartedEvent>(),
            Arg.Any<CancellationToken>());
        runner.Received(1).StreamWorkflowRunAsync(
            Arg.Any<WorkflowRunRequest>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Dispatcher_ConcurrentSameExecutionRequest_RegistersSingleStartAttempt()
    {
        var sessionId = SessionId.NewId();
        var workflow = CreateStartingWorkflow(sessionId, nextRetryAt: null);
        var attemptRegistered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseAttempt = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var workflowService = Substitute.For<IPlanWorkflowApplicationService>();
        workflowService.GetAsync(sessionId, Arg.Any<CancellationToken>()).Returns(workflow);
        workflowService.RegisterStartAttemptAsync(
                Arg.Any<RegisterPlanStartAttemptCommand>(),
                Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                attemptRegistered.SetResult();
                await releaseAttempt.Task;
                return new PlanTransitionResult(workflow with
                {
                    StartAttempt = 1,
                    Version = workflow.Version + 1,
                    NextRetryAt = DateTimeOffset.UtcNow.AddSeconds(1),
                });
            });
        var runner = Substitute.For<IConversationRunner>();
        runner.StreamWorkflowRunAsync(Arg.Any<WorkflowRunRequest>(), Arg.Any<CancellationToken>())
            .Returns(EmptyQueryEvents());
        var session = CreateInteractiveSession(Substitute.For<ISessionManager>(), runner);
        var sut = CreateDispatcher(workflowService);

        var first = sut.StartBuildAsync(session, workflow, TestContext.Current.CancellationToken);
        await attemptRegistered.Task;
        var second = sut.StartBuildAsync(session, workflow, TestContext.Current.CancellationToken);
        releaseAttempt.SetResult();
        await Task.WhenAll(first, second);

        await workflowService.Received(1).RegisterStartAttemptAsync(
            Arg.Any<RegisterPlanStartAttemptCommand>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Dispatcher_ConcurrentRecovery_StreamsSingleBuildRun()
    {
        var sessionId = SessionId.NewId();
        var workflow = CreateExecutingWorkflow(sessionId, PlanWorkflowState.Executing);
        var releaseRun = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var workflowService = Substitute.For<IPlanWorkflowApplicationService>();
        workflowService.GetAsync(sessionId, Arg.Any<CancellationToken>())
            .Returns(workflow, workflow, workflow);
        var runner = Substitute.For<IConversationRunner>();
        runner.StreamWorkflowRunAsync(Arg.Any<WorkflowRunRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ => BlockingQueryEvents(runStarted, releaseRun));
        var sessionManager = Substitute.For<ISessionManager>();
        sessionManager.WorkingDirectory.Returns(_root);
        sessionManager.GetConversation(sessionId).Returns(CreateConversation(sessionId));
        var session = CreateInteractiveSession(sessionManager, runner);
        var sut = CreateDispatcher(workflowService);

        var first = sut.ResumeBuildAsync(session, workflow, TestContext.Current.CancellationToken);
        await runStarted.Task;
        var second = sut.ResumeBuildAsync(session, workflow, TestContext.Current.CancellationToken);
        releaseRun.SetResult();
        await Task.WhenAll(first, second);

        runner.Received(1).StreamWorkflowRunAsync(
            Arg.Any<WorkflowRunRequest>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Dispatcher_PassesApprovedStepsAsPrescribedBuildPlan()
    {
        var sessionId = SessionId.NewId();
        var workflow = CreateStartingWorkflow(sessionId, nextRetryAt: null);
        var attempted = workflow with
        {
            StartAttempt = 1,
            Version = workflow.Version + 1,
            NextRetryAt = DateTimeOffset.UtcNow.AddSeconds(1),
        };
        var executing = attempted with { State = PlanWorkflowState.Executing };
        var workflowService = Substitute.For<IPlanWorkflowApplicationService>();
        workflowService.GetAsync(sessionId, Arg.Any<CancellationToken>())
            .Returns(workflow, executing, executing);
        workflowService.RegisterStartAttemptAsync(
                Arg.Any<RegisterPlanStartAttemptCommand>(),
                Arg.Any<CancellationToken>())
            .Returns(new PlanTransitionResult(attempted));
        var runner = Substitute.For<IConversationRunner>();
        WorkflowRunRequest? captured = null;
        runner.StreamWorkflowRunAsync(
                Arg.Do<WorkflowRunRequest>(request => captured = request),
                Arg.Any<CancellationToken>())
            .Returns(EmptyQueryEvents());
        var sessionManager = Substitute.For<ISessionManager>();
        sessionManager.WorkingDirectory.Returns(_root);
        sessionManager.GetConversation(sessionId).Returns(CreateConversation(sessionId));
        var session = CreateInteractiveSession(sessionManager, runner);
        var sut = CreateDispatcher(workflowService);

        await sut.StartBuildAsync(session, workflow, TestContext.Current.CancellationToken);

        captured.Should().NotBeNull();
        captured!.PrescribedBuildPlan.Should().NotBeNull();
        captured.PrescribedBuildPlan!.RequireExplicitTaskCompletion.Should().BeTrue();
        captured.PrescribedBuildPlan.Tasks.Should().BeEquivalentTo(
            workflow.ApprovedSnapshot!.Steps.Select(step => new BuildPlanTask(
                step.Id,
                step.Title,
                step.Description,
                step.DependsOn,
                step.Files,
                step.AcceptanceCriteria)));
    }

    [Fact]
    public async Task Dispatcher_StartFailureBeforeBuildRun_PreservesStartingExecutionForRetry()
    {
        var sessionId = SessionId.NewId();
        var workflow = CreateStartingWorkflow(sessionId, nextRetryAt: null);
        var attempted = workflow with
        {
            StartAttempt = 1,
            Version = workflow.Version + 1,
            NextRetryAt = DateTimeOffset.UtcNow.AddSeconds(1),
        };
        var workflowService = Substitute.For<IPlanWorkflowApplicationService>();
        workflowService.GetAsync(sessionId, Arg.Any<CancellationToken>())
            .Returns(workflow, attempted);
        workflowService.RegisterStartAttemptAsync(
                Arg.Any<RegisterPlanStartAttemptCommand>(),
                Arg.Any<CancellationToken>())
            .Returns(new PlanTransitionResult(attempted));
        var sessionManager = Substitute.For<ISessionManager>();
        sessionManager.GetConversation(sessionId).Returns(CreateConversation(sessionId));
        sessionManager.SaveAsync(Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new IOException("persistence unavailable"));
        var session = CreateInteractiveSession(sessionManager);
        var sut = CreateDispatcher(workflowService);

        await sut.StartBuildAsync(session, workflow, TestContext.Current.CancellationToken);

        attempted.State.Should().Be(PlanWorkflowState.StartingExecution);
        attempted.NextRetryAt.Should().NotBeNull();
        await workflowService.DidNotReceive().HandleRunEventAsync(
            Arg.Any<BuildRunFailedEvent>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Dispatcher_FifthAttemptExhausted_TransitionsWorkflowToFailed()
    {
        var sessionId = SessionId.NewId();
        var workflow = CreateStartingWorkflow(sessionId, nextRetryAt: null) with { StartAttempt = 5 };
        var workflowService = Substitute.For<IPlanWorkflowApplicationService>();
        workflowService.GetAsync(sessionId, Arg.Any<CancellationToken>()).Returns(workflow);
        var session = CreateInteractiveSession(Substitute.For<ISessionManager>());
        var sut = CreateDispatcher(workflowService);

        await sut.StartBuildAsync(session, workflow, TestContext.Current.CancellationToken);

        await workflowService.Received(1).HandleRunEventAsync(
            Arg.Is<BuildRunFailedEvent>(evt => evt.ErrorCode == "StartRetryExhausted"),
            TestContext.Current.CancellationToken);
        await workflowService.DidNotReceive().RegisterStartAttemptAsync(
            Arg.Any<RegisterPlanStartAttemptCommand>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpdateStepAsync_RejectsBlockedStepAndAllowsItAfterDependencyCompletes()
    {
        var sessionId = SessionId.NewId();
        const string runId = "build-run-dag";
        var now = DateTimeOffset.UtcNow;
        var created = PlanWorkflow.Create(sessionId);
        var workflow = created with
        {
            State = PlanWorkflowState.Executing,
            Version = 1,
            LatestRevision = 1,
            SubmittedRevision = 1,
            ApprovedRevision = 1,
            ActiveRunId = runId,
            ActiveRunKind = PlanRunKind.Build,
            ApprovedSnapshot = new ApprovedPlanSnapshot
            {
                PlanId = created.Id,
                SessionId = sessionId,
                Revision = 1,
                Markdown = "# DAG",
                Steps = [
                    CreateStep("implementation", []),
                    CreateStep("verification", ["implementation"]),
                ],
                ContentHash = "sha256-test",
                ApprovedBy = "user",
                ApprovedAt = now,
            },
            StepExecutions = [
                new PlanStepExecution
                {
                    StepId = "implementation",
                    Status = PlanStepExecutionStatus.InProgress,
                    UpdatedAt = now,
                },
                new PlanStepExecution
                {
                    StepId = "verification",
                    Status = PlanStepExecutionStatus.Pending,
                    UpdatedAt = now,
                },
            ],
            UpdatedAt = now,
        };
        var store = new PlanAggregateStore(_root);
        await store.SaveAsync(new PlanAggregate(workflow, [CreateRevision(workflow)]), -1, TestContext.Current.CancellationToken);
        var sut = new PlanWorkflowApplicationService(store);

        var blocked = async () => await sut.UpdateStepAsync(new UpdatePlanStepCommand(
            "verification-start-blocked",
            sessionId,
            workflow.Id,
            runId,
            "verification",
            PlanStepExecutionStatus.InProgress,
            null,
            null), TestContext.Current.CancellationToken);

        await blocked.Should().ThrowAsync<PlanTransitionException>()
            .WithMessage("*blocked by incomplete dependencies: implementation*");

        var implementation = await sut.UpdateStepAsync(new UpdatePlanStepCommand(
            "implementation-completed",
            sessionId,
            workflow.Id,
            runId,
            "implementation",
            PlanStepExecutionStatus.Completed,
            "implementation evidence",
            null), TestContext.Current.CancellationToken);
        var verification = await sut.UpdateStepAsync(new UpdatePlanStepCommand(
            "verification-started",
            sessionId,
            workflow.Id,
            runId,
            "verification",
            PlanStepExecutionStatus.InProgress,
            null,
            null), TestContext.Current.CancellationToken);

        implementation.Workflow.StepExecutions.Single(step => step.StepId == "implementation")
            .Status.Should().Be(PlanStepExecutionStatus.Completed);
        verification.Workflow.StepExecutions.Single(step => step.StepId == "verification")
            .Status.Should().Be(PlanStepExecutionStatus.InProgress);
    }

    [Fact]
    public void PlanStepValidator_RejectsDependencyCycle()
    {
        var steps = new[]
        {
            CreateStep("one", ["two"]),
            CreateStep("two", ["one"]),
        };

        var act = () => PlanStepValidator.Validate(steps);

        act.Should().Throw<PlanValidationException>()
            .Which.Errors.Should().Contain(error => error.Contains("cycle", StringComparison.OrdinalIgnoreCase));
    }

    private PlanWorkflowApplicationService CreateSut()
        => new(new PlanAggregateStore(_root));

    private static PlanRevision CreateRevision(PlanWorkflow workflow)
        => new()
        {
            PlanId = workflow.Id,
            SessionId = workflow.SessionId,
            Revision = 1,
            Title = "Test plan",
            Markdown = "# Test plan",
            Steps = workflow.ApprovedSnapshot?.Steps ?? [CreateStep("persist-workflow", [])],
            Risks = [],
            Assumptions = [],
            ContentHash = "sha256-test",
            Status = PlanRevisionStatus.Approved,
            CreatedAt = DateTimeOffset.UtcNow,
        };

    private static PlanWorkflow CreateStartingWorkflow(
        SessionId sessionId,
        DateTimeOffset? nextRetryAt)
    {
        var created = PlanWorkflow.Create(sessionId);
        var now = DateTimeOffset.UtcNow;
        return created with
        {
            State = PlanWorkflowState.StartingExecution,
            Version = 1,
            LatestRevision = 1,
            SubmittedRevision = 1,
            ApprovedRevision = 1,
            ExecutionRequestId = Guid.NewGuid().ToString("N"),
            NextRetryAt = nextRetryAt,
            ApprovedSnapshot = new ApprovedPlanSnapshot
            {
                PlanId = created.Id,
                SessionId = sessionId,
                Revision = 1,
                Markdown = "# Approved plan",
                Steps = [CreateStep("persist-workflow", [])],
                ContentHash = "sha256-test",
                ApprovedBy = "user",
                ApprovedAt = now,
            },
            StepExecutions = [new PlanStepExecution
            {
                StepId = "persist-workflow",
                Status = PlanStepExecutionStatus.Pending,
                UpdatedAt = now,
            }],
            UpdatedAt = now,
        };
    }

    private static async Task<PlanWorkflow> CreatePersistedExecutingWorkflowAsync(
        PlanWorkflowApplicationService sut,
        SessionId sessionId)
    {
        var submitted = await sut.SubmitAsync(
            CreateSubmitCommand(sessionId, "plan-run-recovery"),
            TestContext.Current.CancellationToken);
        await sut.HandleRunEventAsync(new PlanRunCompletedEvent(
            sessionId,
            submitted.Workflow.Id,
            "plan-run-recovery",
            ProtocolValid: true,
            DateTimeOffset.UtcNow), TestContext.Current.CancellationToken);
        var awaiting = (await sut.GetAsync(sessionId, TestContext.Current.CancellationToken))!;
        var approved = await sut.ApproveAsync(new ApprovePlanCommand(
            "approve-recovery",
            sessionId,
            awaiting.Id,
            awaiting.SubmittedRevision!.Value,
            awaiting.Version,
            "user"), TestContext.Current.CancellationToken);
        var runId = $"build-{approved.Workflow.ExecutionRequestId}";
        await sut.HandleRunEventAsync(new BuildRunStartedEvent(
            sessionId,
            approved.Workflow.Id,
            runId,
            DateTimeOffset.UtcNow), TestContext.Current.CancellationToken);
        return (await sut.GetAsync(sessionId, TestContext.Current.CancellationToken))!;
    }

    private static PlanWorkflow CreateExecutingWorkflow(
        SessionId sessionId,
        PlanWorkflowState state)
    {
        var workflow = CreateStartingWorkflow(sessionId, nextRetryAt: null);
        return workflow with
        {
            State = state,
            ActiveRunId = $"build-{workflow.ExecutionRequestId}",
            ActiveRunKind = PlanRunKind.Build,
            Version = workflow.Version + 1,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
    }

    private static BuildRun CreateMatchingBuildRun(PlanWorkflow workflow, BuildRunState state)
    {
        var now = DateTimeOffset.UtcNow;
        var snapshot = workflow.ApprovedSnapshot!;
        return new BuildRun
        {
            Id = BuildRunId.New(),
            ConversationId = workflow.SessionId,
            State = state,
            IntakePrompt = "Execute approved plan.",
            Plan = new BuildPlan(
                $"Execute approved plan {workflow.Id} revision {snapshot.Revision}.",
                snapshot.Steps.Select(step => new BuildPlanTask(
                    step.Id,
                    step.Title,
                    step.Description,
                    step.DependsOn,
                    step.Files,
                    step.AcceptanceCriteria)).ToArray(),
                [],
                [],
                [],
                RequireExplicitTaskCompletion: true),
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    private static Conversation CreateConversation(SessionId sessionId)
        => new()
        {
            Id = sessionId,
            WorkingDirectory = Environment.CurrentDirectory,
            Status = ConversationStatus.Active,
        };

    private static PlanAgentRunDispatcher CreateDispatcher(
        IPlanWorkflowApplicationService workflowService)
        => new(
            workflowService,
            new PlanCardPublisher(),
            new TuiInteractionBridge(),
            NullLogger<PlanAgentRunDispatcher>.Instance);

    private static InteractiveSession CreateInteractiveSession(
        ISessionManager sessionManager,
        IConversationRunner? conversationRunner = null)
        => new(
            conversationRunner ?? Substitute.For<IConversationRunner>(),
            "system",
            sessionManager,
            new WorkingModeController(),
            SshHost: null,
            SlashCommands: [],
            Model: "test-model");

    private static async IAsyncEnumerable<QueryEvent> EmptyQueryEvents()
    {
        await Task.CompletedTask;
        yield break;
    }

    private static async IAsyncEnumerable<QueryEvent> SingleQueryEvent(QueryEvent queryEvent)
    {
        await Task.CompletedTask;
        yield return queryEvent;
    }

    private static async IAsyncEnumerable<QueryEvent> BlockingQueryEvents(
        TaskCompletionSource started,
        TaskCompletionSource release)
    {
        started.SetResult();
        await release.Task;
        yield break;
    }

    private static SubmitPlanCommand CreateSubmitCommand(SessionId sessionId, string runId)
        => new(
            $"submit-{runId}",
            sessionId,
            -1,
            "Refactor plan mode",
            "# Refactor plan mode\n\n## Context\nUpdate src/OneCode.App/Tools/CreatePlanTool.cs.\n\n## Approach\nUse a persisted workflow with verification.\n\n## Verification\nRun dotnet test.",
            [CreateStep("persist-workflow", [])],
            [],
            [],
            runId);

    private static PlanStepDefinition CreateStep(string id, IReadOnlyList<string> dependsOn)
        => new()
        {
            Id = id,
            Title = "Persist workflow",
            Description = "Persist and validate the plan workflow.",
            Files = ["src/OneCode.App/Services/PlanMode/PlanAggregateStore.cs"],
            AcceptanceCriteria = ["Workflow can be restored after restart."],
            DependsOn = dependsOn,
            Risk = PlanStepRisk.Low,
        };

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }
}
