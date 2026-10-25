using OneCode.App.Services.Context;
using OneCode.App.Services.Skills;
using OneCode.App.Services.Streaming;

namespace OneCode.Tui;

public sealed record TuiOverlayDependencies(
    PlanCardPublisher PlanCardPublisher,
    OrchestrationEventBus OrchestrationEvents,
    IPlanModeService PlanModeService,
    IPlanWorkflowApplicationService PlanWorkflow,
    AggregateApprovalGate PlanApprovalGate,
    IPlanAggregateStore PlanAggregateStore,
    IPlanAgentRunDispatcher PlanRunDispatcher,
    PlanExecutionRecoveryService PlanExecutionRecovery,
    SkillFilesWatcher SkillFilesWatcher,
    PlanExecutionContextProvider PlanExecutionContextProvider,
    TuiInteractionDispatcher Interaction);
