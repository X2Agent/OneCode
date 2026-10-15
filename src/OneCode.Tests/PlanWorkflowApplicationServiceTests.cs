
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using OneCode.App.Commands;
using OneCode.App.Query;
using OneCode.App.Services;
using OneCode.App.Services.PlanMode;
using OneCode.App.Services.Streaming;
using OneCode.App.Session;
using OneCode.Core.Build;
using OneCode.Core.Config;
using OneCode.Core.Domain;
using OneCode.Core.Models;
using OneCode.Core.PlanMode;
using OneCode.Tests.TestSupport;
namespace OneCode.Tests;

public sealed partial class PlanWorkflowApplicationServiceTests : IDisposable
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
    public async Task SubmitAsync_SameContentWhileFinalizing_ReturnsExistingRevisionWithoutWriting()
    {
        var sut = CreateSut();
        var sessionId = SessionId.NewId();
        var first = await sut.SubmitAsync(
            CreateSubmitCommand(sessionId, "plan-run-1"),
            TestContext.Current.CancellationToken);

        // 模型提交成功后重放同一份计划：不同的命令 ID，期望版本取当前版本（与真实工具一致）。
        var replay = await sut.SubmitAsync(
            CreateSubmitCommand(sessionId, "plan-run-1") with
            {
                CommandId = "submit-replayed",
                ExpectedWorkflowVersion = first.Workflow.Version,
            },
            TestContext.Current.CancellationToken);

        replay.Revision.Revision.Should().Be(first.Revision.Revision);
        replay.Revision.ContentHash.Should().Be(first.Revision.ContentHash);
        replay.Workflow.Version.Should().Be(first.Workflow.Version, "重放不得产生新的工作流版本");
        var aggregate = await new PlanAggregateStore(_root).LoadAsync(sessionId, TestContext.Current.CancellationToken);
        aggregate!.Revisions.Should().ContainSingle("重放不得追加 revision");
    }

    [Fact]
    public async Task SubmitAsync_ChangedContentWhileFinalizing_IsRejectedAsInvalidState()
    {
        var sut = CreateSut();
        var sessionId = SessionId.NewId();
        var first = await sut.SubmitAsync(
            CreateSubmitCommand(sessionId, "plan-run-1"),
            TestContext.Current.CancellationToken);

        var act = () => sut.SubmitAsync(
            CreateSubmitCommand(sessionId, "plan-run-1") with
            {
                CommandId = "submit-changed",
                ExpectedWorkflowVersion = first.Workflow.Version,
                Markdown = "# A different plan\n\n## Context\nChanged scope.",
            },
            TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<PlanTransitionException>(
            "内容变化说明计划确实改了，需要新的规划轮次，不能当作重放吞掉");
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
        cancelled.Workflow.NextRetryAt.Should().BeNull();
    }

    [Fact]
    public async Task ApproveAsync_FreezesOriginalRequestAndClarificationsIntoSnapshot()
    {
        var sut = CreateSut();
        var sessionId = SessionId.NewId();
        var submitted = await sut.SubmitAsync(
            CreateSubmitCommand(sessionId, "plan-run-1"),
            TestContext.Current.CancellationToken);
        await sut.HandleRunEventAsync(
            new PlanRunCompletedEvent(sessionId, submitted.Workflow.Id, "plan-run-1", ProtocolValid: true, DateTimeOffset.UtcNow),
            TestContext.Current.CancellationToken);
        var awaiting = (await sut.GetAsync(sessionId, TestContext.Current.CancellationToken))!;

        var approved = await sut.ApproveAsync(
            new ApprovePlanCommand(
                "approve-1",
                sessionId,
                awaiting.Id,
                awaiting.SubmittedRevision!.Value,
                awaiting.Version,
                "user",
                OriginalRequest: "must run offline",
                Clarifications: ["Q&A: storage? -> sqlite"]),
            TestContext.Current.CancellationToken);

        var reloaded = (await sut.GetAsync(sessionId, TestContext.Current.CancellationToken))!;
        approved.Workflow.ApprovedSnapshot!.OriginalRequest.Should().Be("must run offline");
        reloaded.ApprovedSnapshot!.OriginalRequest.Should().Be("must run offline", "冻结值必须经真实落盘往返保留");
        reloaded.ApprovedSnapshot.Clarifications.Should().Equal("Q&A: storage? -> sqlite");
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
                Title = "Approved plan",
                Markdown = "# Approved plan",
                Steps = [CreateStep("persist-workflow", [])],
                ContentHash = "sha256-test",
                ApprovedBy = "user",
                ApprovedAt = now,
            },
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
                snapshot.Title,
                snapshot.Steps.Select(step => new BuildPlanTask(
                    step.Id,
                    step.Title,
                    step.Description,
                    step.DependsOn,
                    step.Files,
                    step.AcceptanceCriteria)).ToArray(),
                [],
                [],
                []),
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
        IPlanWorkflowApplicationService workflowService,
        IModelManager? modelManager = null,
        IAppStateAccessor? appState = null)
        => new(
            workflowService,
            new PlanCardPublisher(new OrchestrationEventBus()),
            new TuiInteractionBridge(),
            modelManager ?? CreateModelManager("test-model"),
            // 真实访问器：其 Current 恒非 null（默认 AppState 的 MainLoopModel 为 null → 回退配置模型）。
            // 不用 Substitute.For<IAppStateAccessor>() 作默认值——NSubstitute 无法为密封 record
            // 返回类型的只读属性生成非 null 默认值，未配置的 Current 会返回 null。
            appState ?? new AppStateAccessor(),
            NullLogger<PlanAgentRunDispatcher>.Instance);

    /// <summary>配置了主模型的管理器；未显式给定会话覆盖时它就是运行时模型。</summary>
    private static ModelManager CreateModelManager(string configuredModel)
        => new(
            TestConfigManager.Create(new AppSettings { Model = configuredModel }),
            Substitute.For<IModelCatalog>());

    private static InteractiveSession CreateInteractiveSession(
        ISessionManager sessionManager,
        IConversationRunner? conversationRunner = null)
        => new(
            conversationRunner ?? Substitute.For<IConversationRunner>(),
            "system",
            sessionManager,
            new WorkingModeController(),
            SlashCommands: [],
            HarnessPrompt: "harness");

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

