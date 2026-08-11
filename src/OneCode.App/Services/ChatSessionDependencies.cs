using OneCode.App.Query;
using OneCode.App.Services.Compact;
using OneCode.App.Services.Notifier;
using OneCode.App.Services.Observability;
using OneCode.App.Services.PlanMode;
using OneCode.App.Services.BuildMode;
using OneCode.App.Session;
using OneCode.Infrastructure.Config;

namespace OneCode.App.Services;

/// <summary>Session/config stack for interactive chat.</summary>
public sealed record ChatSessionDependencies(
    ISessionManager SessionManager,
    ISessionToolSetManager SessionToolSetManager,
    IToolCapabilityResolver ToolCapabilityResolver,
    IConfigManager ConfigManager,
    IPlanWorkflowApplicationService? PlanWorkflow = null,
    IToolProtocolValidator? ToolProtocolValidator = null,
    PlanCardPublisher? PlanCardPublisher = null,
    IBuildRunCoordinator? BuildRunCoordinator = null,
    IClarificationInteractionService? ClarificationInteraction = null,
    ControlledBuildAttemptHost? ControlledBuildAttemptHost = null,
    OneCode.Core.Build.IBuildRunStore? BuildRunStore = null,
    OneCode.Core.Workflows.IOperationLedger? OperationLedger = null);

/// <summary>Token/cost/notification observability for interactive chat.</summary>
public sealed record ChatObservabilityDependencies(
    ITokenUsageTracker TokenUsageTracker,
    ITokenBreakdownEstimator TokenBreakdownEstimator,
    INotifierService NotifierService);
