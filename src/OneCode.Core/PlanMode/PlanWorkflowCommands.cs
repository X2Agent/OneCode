using OneCode.Core.Domain;

namespace OneCode.Core.PlanMode;

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
    DateTimeOffset AttemptedAt,
    long FencingToken = 0);

public sealed record BindPlanBuildRunCommand(
    string CommandId,
    SessionId SessionId,
    PlanWorkflowId PlanId,
    string RunId,
    string BuildRunId,
    long FencingToken = 0);

public sealed record FailPlanExecutionRecoveryCommand(
    string CommandId,
    SessionId SessionId,
    PlanWorkflowId PlanId,
    long ExpectedWorkflowVersion,
    string ErrorCode,
    string ErrorMessage,
    DateTimeOffset FailedAt);

/// <summary>
/// Plan 代理运行事件基类。<see cref="FencingToken"/> 携带执行世代的 fencing 令牌：
/// 执行期事件（BuildRun*）由 dispatcher 在 claim 后发放并穿透到持久化；
/// 规划期事件（PlanRun*）为 0，写入走未 claim 的普通保存。
/// </summary>
public abstract record PlanAgentRunEvent(
    SessionId SessionId,
    PlanWorkflowId PlanId,
    string RunId,
    DateTimeOffset OccurredAt,
    long FencingToken = 0);

public sealed record PlanRunCompletedEvent(
    SessionId SessionId,
    PlanWorkflowId PlanId,
    string RunId,
    bool ProtocolValid,
    DateTimeOffset OccurredAt,
    long FencingToken = 0)
    : PlanAgentRunEvent(SessionId, PlanId, RunId, OccurredAt, FencingToken);

public sealed record PlanRunFailedEvent(
    SessionId SessionId,
    PlanWorkflowId PlanId,
    string RunId,
    string ErrorCode,
    string ErrorMessage,
    DateTimeOffset OccurredAt,
    long FencingToken = 0)
    : PlanAgentRunEvent(SessionId, PlanId, RunId, OccurredAt, FencingToken);

public sealed record BuildRunStartedEvent(
    SessionId SessionId,
    PlanWorkflowId PlanId,
    string RunId,
    DateTimeOffset OccurredAt,
    long FencingToken = 0)
    : PlanAgentRunEvent(SessionId, PlanId, RunId, OccurredAt, FencingToken);

public sealed record BuildRunFailedEvent(
    SessionId SessionId,
    PlanWorkflowId PlanId,
    string RunId,
    string ErrorCode,
    string ErrorMessage,
    DateTimeOffset OccurredAt,
    long FencingToken = 0)
    : PlanAgentRunEvent(SessionId, PlanId, RunId, OccurredAt, FencingToken);

public sealed record UpdatePlanStepCommand(
    string CommandId,
    SessionId SessionId,
    PlanWorkflowId PlanId,
    string RunId,
    string StepId,
    PlanStepExecutionStatus Status,
    string? Evidence,
    string? Error,
    long FencingToken = 0);

public sealed record CompletePlanExecutionCommand(
    string CommandId,
    SessionId SessionId,
    PlanWorkflowId PlanId,
    string RunId,
    string Summary,
    long FencingToken = 0);

public sealed record CompletePlanVerificationCommand(
    string CommandId,
    SessionId SessionId,
    PlanWorkflowId PlanId,
    string RunId,
    bool Passed,
    IReadOnlyList<string> Evidence,
    string Summary,
    long FencingToken = 0);

public sealed record PlanRevisionResult(PlanWorkflow Workflow, PlanRevision Revision);
public sealed record PlanSubmissionResult(PlanWorkflow Workflow, PlanRevision Revision);
public sealed record PlanTransitionResult(PlanWorkflow Workflow, bool IsDuplicateCommand = false);
