using System.Text.RegularExpressions;
using OneCode.Core.Build;
using OneCode.Core.Domain;
using OneCode.Core.Workflows;

namespace OneCode.Core.PlanMode;

public readonly partial record struct PlanWorkflowId(string Value)
{
    public static PlanWorkflowId NewId() => new(Guid.NewGuid().ToString("N"));
    public override string ToString() => Value;

    public static PlanWorkflowId? TryParse(string? value)
        => !string.IsNullOrWhiteSpace(value) && SafePattern().IsMatch(value)
            ? new PlanWorkflowId(value)
            : null;

    [GeneratedRegex("^[0-9a-f]{32}$", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex SafePattern();
}

public enum PlanWorkflowState
{
    Planning,
    FinalizingPlanRun,
    AwaitingApproval,
    StartingExecution,
    Executing,
    Verifying,
    Completed,
    Failed,
    Cancelled,
}

public enum PlanRunKind
{
    Planning,
    Build,
    Verification,
}

public enum PlanRevisionStatus
{
    Submitted,
    Superseded,
    Approved,
    Rejected,
}

public enum PlanStepRisk
{
    Low,
    Medium,
    High,
}

public enum PlanStepExecutionStatus
{
    Pending,
    InProgress,
    Completed,
    Failed,
    Skipped,
    Cancelled,
}

public enum PlanFeedbackKind
{
    Rejected,
    EditRequested,
}

public sealed record PlanStepDefinition
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required string Description { get; init; }
    public required IReadOnlyList<string> Files { get; init; }
    public required IReadOnlyList<string> AcceptanceCriteria { get; init; }
    public required IReadOnlyList<string> DependsOn { get; init; }
    public required PlanStepRisk Risk { get; init; }
}

public sealed record PlanStepExecution
{
    public required string StepId { get; init; }
    public required PlanStepExecutionStatus Status { get; init; }
    public string? Evidence { get; init; }
    public string? Error { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }
}

public sealed record PlanRevision
{
    public required PlanWorkflowId PlanId { get; init; }
    public required SessionId SessionId { get; init; }
    public required int Revision { get; init; }
    public required string Title { get; init; }
    public required string Markdown { get; init; }
    public required IReadOnlyList<PlanStepDefinition> Steps { get; init; }
    public required IReadOnlyList<string> Risks { get; init; }
    public required IReadOnlyList<string> Assumptions { get; init; }
    public required string ContentHash { get; init; }
    public required PlanRevisionStatus Status { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
}

/// <summary>
/// 已批准计划快照（计划内容唯一定义源）。
/// <see cref="Project"/> 提供到 Build 执行词汇的权威投影——
/// BuildRun.Plan 等派生缓存一律由本投影生成，恢复时以 PlansMatch 校验锚定。
/// </summary>
public sealed record ApprovedPlanSnapshot
{
    public required PlanWorkflowId PlanId { get; init; }
    public required SessionId SessionId { get; init; }
    public required int Revision { get; init; }
    /// <summary>计划标题（首个 markdown 标题派生，审批时随快照冻结）。</summary>
    public required string Title { get; init; }
    public required string Markdown { get; init; }
    public required IReadOnlyList<PlanStepDefinition> Steps { get; init; }
    public required string ContentHash { get; init; }
    public required string ApprovedBy { get; init; }
    public required DateTimeOffset ApprovedAt { get; init; }

    /// <summary>
    /// 权威投影：ApprovedSnapshot → BuildPlan（原 PlanAgentRunDispatcher.ToBuildPlan 内联实现）。
    /// 派生缓存单源：BuildRun.Plan 必须与该投影一致（恢复时经 BuildResumePolicy 的
    /// PlansMatch 以 prescribed = Project(snapshot) 校验）。
    /// </summary>
    public BuildPlan Project() => new(
        Title,
        Steps.Select(step => new BuildPlanTask(
            step.Id,
            step.Title,
            step.Description,
            step.DependsOn,
            step.Files,
            step.AcceptanceCriteria)).ToArray(),
        [],
        [],
        [],
        RequireExplicitTaskCompletion: true);
}

public sealed record PlanFeedback(
    PlanFeedbackKind Kind,
    string Comment,
    IReadOnlyList<string> StepIds,
    int BasedOnRevision,
    DateTimeOffset CreatedAt);

/// <summary>
/// Plan 工作流聚合根。实现 <see cref="IWorkflowRun"/> 共享词汇与
/// <see cref="ICommandIdempotent"/> 命令幂等能力。
/// </summary>
public sealed record PlanWorkflow : IWorkflowRun, ICommandIdempotent
{
    public required PlanWorkflowId Id { get; init; }
    public required SessionId SessionId { get; init; }
    public required PlanWorkflowState State { get; init; }
    public required long Version { get; init; }
    public required int LatestRevision { get; init; }
    public int? SubmittedRevision { get; init; }
    public int? ApprovedRevision { get; init; }
    public string? ActiveRunId { get; init; }
    public PlanRunKind? ActiveRunKind { get; init; }
    public string? ExecutionRequestId { get; init; }
    public string? BuildRunId { get; init; }
    public int StartAttempt { get; init; }
    public DateTimeOffset? NextRetryAt { get; init; }
    public ApprovedPlanSnapshot? ApprovedSnapshot { get; init; }
    public PlanFeedback? PendingFeedback { get; init; }
    public IReadOnlyList<PlanStepExecution> StepExecutions { get; init; } = [];
    public IReadOnlyList<string> VerificationEvidence { get; init; } = [];
    public string? CompletionSummary { get; init; }
    /// <summary>最近一次成功处理的命令标识（命令幂等，内核能力）。</summary>
    public string? LastProcessedCommandId { get; init; }
    public int? LastProcessedRevision { get; init; }
    public string? LastErrorCode { get; init; }
    public string? LastErrorMessage { get; init; }
    /// <summary>当前 Workflow lease 令牌（接入）。null 表示尚未被 claim；
    /// v1 持久化快照无此字段，按未 claim 处理。</summary>
    public long? WorkflowFencingToken { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }

    public static PlanWorkflow Create(SessionId sessionId, string? activeRunId = null)
    {
        var now = DateTimeOffset.UtcNow;
        return new PlanWorkflow
        {
            Id = PlanWorkflowId.NewId(),
            SessionId = sessionId,
            State = PlanWorkflowState.Planning,
            Version = 0,
            LatestRevision = 0,
            ActiveRunId = activeRunId,
            ActiveRunKind = PlanRunKind.Planning,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }
}
