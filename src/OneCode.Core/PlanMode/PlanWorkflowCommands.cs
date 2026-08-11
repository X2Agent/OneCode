using OneCode.Core.Domain;

namespace OneCode.Core.PlanMode;

public sealed record SavePlanDraftCommand(
    string CommandId,
    SessionId SessionId,
    long ExpectedWorkflowVersion,
    string Title,
    string Markdown,
    IReadOnlyList<PlanStepDefinition> Steps,
    IReadOnlyList<string> Risks,
    IReadOnlyList<string> Assumptions,
    string? ActiveRunId = null);

public sealed record SubmitPlanCommand(
    string CommandId,
    SessionId SessionId,
    long ExpectedWorkflowVersion,
    string Title,
    string Markdown,
    IReadOnlyList<PlanStepDefinition> Steps,
    IReadOnlyList<string> Risks,
    IReadOnlyList<string> Assumptions,
    string ActiveRunId);

public sealed record ApprovePlanCommand(
    string CommandId,
    SessionId SessionId,
    PlanWorkflowId PlanId,
    int Revision,
    long ExpectedWorkflowVersion,
    string ApprovedBy);

public sealed record RejectPlanCommand(
    string CommandId,
    SessionId SessionId,
    PlanWorkflowId PlanId,
    int Revision,
    long ExpectedWorkflowVersion,
    string Reason);

public sealed record RequestPlanEditCommand(
    string CommandId,
    SessionId SessionId,
    PlanWorkflowId PlanId,
    int Revision,
    long ExpectedWorkflowVersion,
    string Feedback,
    IReadOnlyList<string> StepIds);

public sealed record CancelPlanCommand(
    string CommandId,
    SessionId SessionId,
    PlanWorkflowId PlanId,
    long ExpectedWorkflowVersion,
    string Reason);

public sealed record RegisterPlanStartAttemptCommand(
    string CommandId,
    SessionId SessionId,
    PlanWorkflowId PlanId,
    long ExpectedWorkflowVersion,
    DateTimeOffset AttemptedAt);

public sealed record BindPlanBuildRunCommand(
    string CommandId,
    SessionId SessionId,
    PlanWorkflowId PlanId,
    string RunId,
    string BuildRunId);

public sealed record FailPlanExecutionRecoveryCommand(
    string CommandId,
    SessionId SessionId,
    PlanWorkflowId PlanId,
    long ExpectedWorkflowVersion,
    string ErrorCode,
    string ErrorMessage,
    DateTimeOffset FailedAt);

public abstract record PlanAgentRunEvent(
    SessionId SessionId,
    PlanWorkflowId PlanId,
    string RunId,
    DateTimeOffset OccurredAt);

public sealed record PlanRunCompletedEvent(
    SessionId SessionId,
    PlanWorkflowId PlanId,
    string RunId,
    bool ProtocolValid,
    DateTimeOffset OccurredAt)
    : PlanAgentRunEvent(SessionId, PlanId, RunId, OccurredAt);

public sealed record PlanRunFailedEvent(
    SessionId SessionId,
    PlanWorkflowId PlanId,
    string RunId,
    string ErrorCode,
    string ErrorMessage,
    DateTimeOffset OccurredAt)
    : PlanAgentRunEvent(SessionId, PlanId, RunId, OccurredAt);

public sealed record BuildRunStartedEvent(
    SessionId SessionId,
    PlanWorkflowId PlanId,
    string RunId,
    DateTimeOffset OccurredAt)
    : PlanAgentRunEvent(SessionId, PlanId, RunId, OccurredAt);

public sealed record BuildRunFailedEvent(
    SessionId SessionId,
    PlanWorkflowId PlanId,
    string RunId,
    string ErrorCode,
    string ErrorMessage,
    DateTimeOffset OccurredAt)
    : PlanAgentRunEvent(SessionId, PlanId, RunId, OccurredAt);

public sealed record UpdatePlanStepCommand(
    string CommandId,
    SessionId SessionId,
    PlanWorkflowId PlanId,
    string RunId,
    string StepId,
    PlanStepExecutionStatus Status,
    string? Evidence,
    string? Error);

public sealed record CompletePlanExecutionCommand(
    string CommandId,
    SessionId SessionId,
    PlanWorkflowId PlanId,
    string RunId,
    string Summary);

public sealed record CompletePlanVerificationCommand(
    string CommandId,
    SessionId SessionId,
    PlanWorkflowId PlanId,
    string RunId,
    bool Passed,
    IReadOnlyList<string> Evidence,
    string Summary);

public sealed record PlanRevisionResult(PlanWorkflow Workflow, PlanRevision Revision);
public sealed record PlanSubmissionResult(PlanWorkflow Workflow, PlanRevision Revision);
public sealed record PlanTransitionResult(PlanWorkflow Workflow, bool IsDuplicateCommand = false);
