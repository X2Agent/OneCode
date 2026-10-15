// PlanWorkflowApplicationServiceTests partial：可恢复工作流扫描与恢复调度（原文件按职责区块的纯文本搬移）
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using OneCode.App.Services;
using OneCode.App.Services.Compact;
using OneCode.App.Services.PlanMode;
using OneCode.App.Session;
using OneCode.Core.Build;
using OneCode.Core.Domain;
using OneCode.Core.PlanMode;
namespace OneCode.Tests;

public sealed partial class PlanWorkflowApplicationServiceTests
{
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
        var store = new PlanAggregateStore(_root);
        await store.SaveAsync(
            new PlanAggregate(executing, [CreateRevision(executing)]),
            expectedVersion: -1,
            TestContext.Current.CancellationToken);

        var finalizingSession = SessionId.NewId();
        var finalizing = await sut.SubmitAsync(
            CreateSubmitCommand(finalizingSession, "plan-run-finalizing"),
            TestContext.Current.CancellationToken);

        var recoverable = await store.LoadRecoverableExecutionAsync(TestContext.Current.CancellationToken);

        recoverable.Should().HaveCount(3, "FinalizingPlanRun（旧进程未收口）、StartingExecution、Executing 均需对账");
        recoverable.Should().ContainEquivalentOf(starting.Workflow);
        recoverable.Should().ContainEquivalentOf(executing);
        recoverable.Should().ContainEquivalentOf(finalizing.Workflow);
    }

    [Fact]
    public async Task RecoveryService_OrphanedFinalizingWorkflow_AdvancesToAwaitingApprovalThroughRealStore()
    {
        // 走真实写入端：先由真实状态机提交计划（停在 FinalizingPlanRun），再由恢复服务经真实存储推进。
        var sessionId = SessionId.NewId();
        var workflowService = CreateSut();
        await workflowService.SubmitAsync(
            CreateSubmitCommand(sessionId, "plan-run-orphan"),
            TestContext.Current.CancellationToken);
        var sessionManager = Substitute.For<ISessionManager>();
        sessionManager.WorkingDirectory.Returns(_root);
        sessionManager.GetConversation(sessionId).Returns(CreateConversation(sessionId));

        // 「重启」：恢复服务在孤儿计划提交之后才诞生，该计划因此属于旧进程。
        await Task.Delay(20, TestContext.Current.CancellationToken);
        var sut = CreateRecoveryService(workflowService, sessionManager);

        // 同进程内的活跃 run：计划提交晚于服务诞生，FinalizingPlanRun 是正常中间态，不得被抢先收口。
        await Task.Delay(20, TestContext.Current.CancellationToken);
        var liveSession = SessionId.NewId();
        await workflowService.SubmitAsync(
            CreateSubmitCommand(liveSession, "plan-run-live"),
            TestContext.Current.CancellationToken);
        sessionManager.GetConversation(liveSession).Returns(CreateConversation(liveSession));

        await sut.ScanDueAsync(
            CreateInteractiveSession(sessionManager), DateTimeOffset.UtcNow, TestContext.Current.CancellationToken);

        var recovered = await workflowService.GetAsync(sessionId, TestContext.Current.CancellationToken);
        recovered!.State.Should().Be(PlanWorkflowState.AwaitingApproval, "旧进程遗留的已提交计划必须可审批");
        var live = await workflowService.GetAsync(liveSession, TestContext.Current.CancellationToken);
        live!.State.Should().Be(PlanWorkflowState.FinalizingPlanRun, "进程内活跃 run 的提交不是孤儿");
    }

    private PlanExecutionRecoveryService CreateRecoveryService(
        IPlanWorkflowApplicationService workflowService,
        ISessionManager sessionManager)
        => new(
            new PlanAggregateStore(_root),
            workflowService,
            Substitute.For<IBuildRunStore>(),
            Substitute.For<IPlanAgentRunDispatcher>(),
            new PlanRunCloser(sessionManager, new ToolProtocolValidator(), workflowService),
            NullLogger<PlanExecutionRecoveryService>.Instance);

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
            new PlanRunCloser(sessionManager, new ToolProtocolValidator(), workflowService),
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
        workflowService.ClaimExecutionAsync(
                Arg.Any<SessionId>(),
                Arg.Any<PlanWorkflowId>(),
                Arg.Any<long>(),
                Arg.Any<CancellationToken>())
            .Returns(workflow);
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
            new PlanRunCloser(sessionManager, new ToolProtocolValidator(), workflowService),
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
    public async Task RecoveryService_CompletedBuildRun_CompletesPlanWorkflow()
    {
        var sessionId = SessionId.NewId();
        var workflow = CreateExecutingWorkflow(sessionId, PlanWorkflowState.Executing);
        var buildRun = CreateMatchingBuildRun(workflow, BuildRunState.Completed);
        workflow = workflow with { BuildRunId = buildRun.Id.ToString() };
        var store = Substitute.For<IPlanAggregateStore>();
        store.LoadRecoverableExecutionAsync(Arg.Any<CancellationToken>()).Returns([workflow]);
        var workflowService = Substitute.For<IPlanWorkflowApplicationService>();
        workflowService.ClaimExecutionAsync(
                Arg.Any<SessionId>(),
                Arg.Any<PlanWorkflowId>(),
                Arg.Any<long>(),
                Arg.Any<CancellationToken>())
            .Returns(workflow);
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
            new PlanRunCloser(sessionManager, new ToolProtocolValidator(), workflowService),
            NullLogger<PlanExecutionRecoveryService>.Instance);

        await sut.ScanDueAsync(session, DateTimeOffset.UtcNow, TestContext.Current.CancellationToken);

        await workflowService.Received(1).HandleRunEventAsync(
            Arg.Is<PlanAgentRunEvent>(e => e is BuildRunSucceededEvent && e.PlanId == workflow.Id),
            TestContext.Current.CancellationToken);
        await workflowService.DidNotReceive().FailExecutionRecoveryAsync(
            Arg.Any<FailPlanExecutionRecoveryCommand>(),
            Arg.Any<CancellationToken>());
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
    public async Task BindBuildRunAsync_SameRunAfterCompletion_IsIdempotent()
    {
        // 成功收尾的固定时序：plan workflow 先随 BuildRun 完成（Completed、ActiveRunId 清空），
        // BuildRun 的 Verifying 投影事件随后到达触发第二次绑定——同 run 重绑必须幂等
        // 成功，不得被状态守卫误判为失败（否则批准执行全链报 "cannot bind" 错误）。
        var sessionId = SessionId.NewId();
        var store = new PlanAggregateStore(Path.Combine(_root, Guid.NewGuid().ToString("N")));
        var sut = new PlanWorkflowApplicationService(store);
        var executing = await CreatePersistedExecutingWorkflowAsync(sut, sessionId);
        var buildRunId = BuildRunId.New().ToString();
        var first = await sut.BindBuildRunAsync(new BindPlanBuildRunCommand(
            $"bind-{buildRunId}",
            sessionId,
            executing.Id,
            executing.ActiveRunId!,
            buildRunId), TestContext.Current.CancellationToken);
        first.Workflow.BuildRunId.Should().Be(buildRunId);

        // 模拟验证完成：Completed、ActiveRunId 清空（单槽幂等标记也已被其他命令覆盖）。
        var current = (await store.LoadAsync(sessionId, TestContext.Current.CancellationToken))!;
        var completed = current.Workflow with
        {
            State = PlanWorkflowState.Completed,
            ActiveRunId = null,
            Version = current.Workflow.Version + 1,
            LastProcessedCommandId = "later-command",
        };
        await store.SaveAsync(current with { Workflow = completed }, current.Workflow.Version, TestContext.Current.CancellationToken);

        var rebound = await sut.BindBuildRunAsync(new BindPlanBuildRunCommand(
            "bind-after-completion",
            sessionId,
            executing.Id,
            "build-durable",
            buildRunId), TestContext.Current.CancellationToken);

        rebound.IsDuplicateCommand.Should().BeTrue();
        rebound.Workflow.BuildRunId.Should().Be(buildRunId);
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
        workflowService.ClaimExecutionAsync(
                Arg.Any<SessionId>(),
                Arg.Any<PlanWorkflowId>(),
                Arg.Any<long>(),
                Arg.Any<CancellationToken>())
            .Returns(workflow);
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
            new PlanRunCloser(sessionManager, new ToolProtocolValidator(), workflowService),
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
        workflowService.ClaimExecutionAsync(
                Arg.Any<SessionId>(),
                Arg.Any<PlanWorkflowId>(),
                Arg.Any<long>(),
                Arg.Any<CancellationToken>())
            .Returns(callInfo => callInfo.ArgAt<SessionId>(0) == firstSession ? mismatched : recoverable);
        var buildRunStore = Substitute.For<IBuildRunStore>();
        buildRunStore.LoadAsync(firstSession, Arg.Any<CancellationToken>())
            .Returns(CreateMatchingBuildRun(mismatched, BuildRunState.Implementing) with
            {
                Plan = new BuildPlan("wrong", [], [], [], []),
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
            new PlanRunCloser(sessionManager, new ToolProtocolValidator(), workflowService),
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
            new PlanRunCloser(sessionManager, new ToolProtocolValidator(), workflowService),
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
}





