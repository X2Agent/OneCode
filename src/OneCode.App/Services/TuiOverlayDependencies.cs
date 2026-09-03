using OneCode.App.Services.BuildMode;
using OneCode.App.Services.Context;

using OneCode.App.Services.Skills;
using OneCode.App.Services.PlanMode;
using OneCode.App.Services.Streaming;

namespace OneCode.App.Services;

public sealed record TuiOverlayDependencies(
    PlanCardPublisher PlanCardPublisher,
    OrchestrationEventBus OrchestrationEvents,
    IPlanModeService PlanModeService,
    IPlanWorkflowApplicationService PlanWorkflow,
    AggregateApprovalGate PlanApprovalGate,
    IPlanAggregateStore PlanAggregateStore,
    IPlanAgentRunDispatcher PlanRunDispatcher,
    PlanExecutionRecoveryService PlanExecutionRecovery,
    SkillChangeWatcher SkillChangeWatcher,
    BuildModeAttachmentProvider BuildModeAttachmentProvider,
    PlanExecutionContextProvider PlanExecutionContextProvider);
