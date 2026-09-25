using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using OneCode.App.Query;
using OneCode.App.Services;
using OneCode.App.Services.PlanMode;
using OneCode.App.Services.Streaming;
using OneCode.App.Session;
using OneCode.App.Tui;
using OneCode.Core.Coordinator;
using OneCode.Core.Domain;
using OneCode.Core.PlanMode;

namespace OneCode.Tests;

/// <summary>
/// <see cref="AggregateApprovalGate"/> 控制面行为——
/// 决策编排（状态校验/命令构造）、投影发布与批准后的 Build 派发。
/// </summary>
public sealed class AggregateApprovalGateTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "OneCodeAggregateApprovalGateTests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task DecideAsync_Approve_PublishesProjectionAndDispatchesBuild()
    {
        var sut = CreateSut();
        var sessionId = SessionId.NewId();
        await CreateAwaitingApprovalAsync(sut, sessionId);
        var published = new List<PlanWorkflow>();
        var publisher = CreatePublisher(published);
        var dispatcher = Substitute.For<IPlanAgentRunDispatcher>();
        var session = CreateInteractiveSession(CreateSessionManager(sessionId));
        var gate = new AggregateApprovalGate(
            sut,
            publisher,
            dispatcher,
            NullLogger<AggregateApprovalGate>.Instance);

        var outcome = await gate.DecideAsync(
            session,
            PlanCardDecision.Approve,
            TestContext.Current.CancellationToken);

        outcome.Workflow.State.Should().Be(PlanWorkflowState.StartingExecution);
        published.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(outcome.Workflow);
        await dispatcher.Received(1).StartBuildAsync(
            session,
            outcome.Workflow,
            TestContext.Current.CancellationToken);
        var restored = await sut.GetAsync(sessionId, TestContext.Current.CancellationToken);
        restored!.State.Should().Be(PlanWorkflowState.StartingExecution);
        restored!.ApprovedSnapshot.Should().NotBeNull();
    }

    [Fact]
    public async Task DecideAsync_Reject_TransitionsToPlanningWithoutBuildDispatch()
    {
        var sut = CreateSut();
        var sessionId = SessionId.NewId();
        await CreateAwaitingApprovalAsync(sut, sessionId);
        var published = new List<PlanWorkflow>();
        var publisher = CreatePublisher(published);
        var dispatcher = Substitute.For<IPlanAgentRunDispatcher>();
        var session = CreateInteractiveSession(CreateSessionManager(sessionId));
        var gate = new AggregateApprovalGate(
            sut,
            publisher,
            dispatcher,
            NullLogger<AggregateApprovalGate>.Instance);

        var outcome = await gate.DecideAsync(
            session,
            PlanCardDecision.Reject,
            TestContext.Current.CancellationToken);

        outcome.Workflow.State.Should().Be(PlanWorkflowState.Planning);
        outcome.Workflow.PendingFeedback!.Kind.Should().Be(PlanFeedbackKind.Rejected);
        published.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(outcome.Workflow);
        await dispatcher.DidNotReceive().StartBuildAsync(
            Arg.Any<InteractiveSession>(),
            Arg.Any<PlanWorkflow>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DecideAsync_Edit_RequestsRevisionWithoutBuildDispatch()
    {
        var sut = CreateSut();
        var sessionId = SessionId.NewId();
        await CreateAwaitingApprovalAsync(sut, sessionId);
        var published = new List<PlanWorkflow>();
        var publisher = CreatePublisher(published);
        var dispatcher = Substitute.For<IPlanAgentRunDispatcher>();
        var session = CreateInteractiveSession(CreateSessionManager(sessionId));
        var gate = new AggregateApprovalGate(
            sut,
            publisher,
            dispatcher,
            NullLogger<AggregateApprovalGate>.Instance);

        var outcome = await gate.DecideAsync(
            session,
            PlanCardDecision.Edit,
            TestContext.Current.CancellationToken);

        outcome.Workflow.State.Should().Be(PlanWorkflowState.Planning);
        outcome.Workflow.PendingFeedback.Should().NotBeNull();
        published.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(outcome.Workflow);
        await dispatcher.DidNotReceive().StartBuildAsync(
            Arg.Any<InteractiveSession>(),
            Arg.Any<PlanWorkflow>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DecideAsync_WhenWorkflowMissing_PropagatesAndSkipsPublishAndDispatch()
    {
        var sut = CreateSut();
        var published = new List<PlanWorkflow>();
        var publisher = CreatePublisher(published);
        var dispatcher = Substitute.For<IPlanAgentRunDispatcher>();
        var session = CreateInteractiveSession(CreateSessionManager(SessionId.NewId()));
        var gate = new AggregateApprovalGate(
            sut,
            publisher,
            dispatcher,
            NullLogger<AggregateApprovalGate>.Instance);

        var act = () => gate.DecideAsync(
            session,
            PlanCardDecision.Approve,
            TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>();
        published.Should().BeEmpty();
        await dispatcher.DidNotReceive().StartBuildAsync(
            Arg.Any<InteractiveSession>(),
            Arg.Any<PlanWorkflow>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DecideAsync_PublishesOutcomeAndDispatchesOnlyOnApprove()
    {
        // 门控语义单元级验证：发布始终发生，Build 派发仅在 Approve 分支。
        var workflowService = Substitute.For<IPlanWorkflowApplicationService>();
        var sessionId = SessionId.NewId();
        var workflow = PlanWorkflow.Create(sessionId) with
        {
            State = PlanWorkflowState.AwaitingApproval,
            LatestRevision = 1,
            SubmittedRevision = 1,
        };
        workflowService.DecideAsync(
                Arg.Any<InteractiveSession>(),
                Arg.Any<PlanCardDecision>(),
                Arg.Any<CancellationToken>())
            .Returns(new DecisionOutcome(workflow));
        var published = new List<PlanWorkflow>();
        var publisher = CreatePublisher(published);
        var dispatcher = Substitute.For<IPlanAgentRunDispatcher>();
        var session = CreateInteractiveSession(CreateSessionManager(sessionId));
        var gate = new AggregateApprovalGate(
            workflowService,
            publisher,
            dispatcher,
            NullLogger<AggregateApprovalGate>.Instance);

        var rejected = await gate.DecideAsync(
            session,
            PlanCardDecision.Reject,
            TestContext.Current.CancellationToken);
        var approved = await gate.DecideAsync(
            session,
            PlanCardDecision.Approve,
            TestContext.Current.CancellationToken);

        rejected.Workflow.Id.Should().Be(workflow.Id);
        approved.Workflow.Id.Should().Be(workflow.Id);
        published.Should().HaveCount(2);
        await dispatcher.Received(1).StartBuildAsync(
            Arg.Any<InteractiveSession>(),
            Arg.Any<PlanWorkflow>(),
            Arg.Any<CancellationToken>());
    }

    private PlanWorkflowApplicationService CreateSut()
        => new(new PlanAggregateStore(_root));

    private static PlanCardPublisher CreatePublisher(List<PlanWorkflow> published)
    {
        // PlanCardPublisher 是统一总线（OrchestrationEventBus）的发射器，
        // 断言面从订阅 publisher 事件改为订阅总线上的 PlanProjectionChanged。
        var bus = new OrchestrationEventBus();
        bus.Subscribe(evt =>
        {
            if (evt is OrchestrationEvent.PlanProjectionChanged changed)
                published.Add(changed.Workflow);
        });
        return new PlanCardPublisher(bus);
    }

    private static async Task<PlanWorkflow> CreateAwaitingApprovalAsync(
        PlanWorkflowApplicationService sut,
        SessionId sessionId)
    {
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
        return (await sut.GetAsync(sessionId, TestContext.Current.CancellationToken))!;
    }

    private static ISessionManager CreateSessionManager(SessionId sessionId)
    {
        var sessionManager = Substitute.For<ISessionManager>();
        sessionManager.ForegroundConversation.Returns(CreateConversation(sessionId));
        return sessionManager;
    }

    private static Conversation CreateConversation(SessionId sessionId)
        => new()
        {
            Id = sessionId,
            WorkingDirectory = Environment.CurrentDirectory,
            Status = ConversationStatus.Active,
        };

    private static InteractiveSession CreateInteractiveSession(ISessionManager sessionManager)
        => new(
            Substitute.For<IConversationRunner>(),
            "system",
            sessionManager,
            new WorkingModeController(),
            SshHost: null,
            SlashCommands: [],
            HarnessPrompt: "harness");

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
