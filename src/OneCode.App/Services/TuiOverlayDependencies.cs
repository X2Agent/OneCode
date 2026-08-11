using OneCode.App.Services.BuildMode;
using OneCode.App.Services.PlanMode;
using OneCode.App.Services.Skills;

namespace OneCode.App.Services;

public sealed record TuiOverlayDependencies(
    PlanCardPublisher PlanCardPublisher,
    IPlanModeService PlanModeService,
    IPlanWorkflowApplicationService PlanWorkflow,
    IPlanAggregateStore PlanAggregateStore,
    IPlanAgentRunDispatcher PlanRunDispatcher,
    PlanExecutionRecoveryService PlanExecutionRecovery,
    SkillChangeWatcher SkillChangeWatcher,
    BuildModeAttachmentProvider BuildModeAttachmentProvider,
    PlanExecutionContextProvider PlanExecutionContextProvider);
