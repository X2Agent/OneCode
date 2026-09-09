using OneCode.Core.Domain;
using OneCode.Core.Workflows;

namespace OneCode.Core.Goals;

public readonly record struct GoalRunId(string Value)
{
    public static GoalRunId New() => new(Guid.NewGuid().ToString("N"));
    public override string ToString() => Value;
}

public enum GoalRunState
{
    Planning,
    Executing,
    Validating,
    Publishing,
    Paused,
    Blocked,
    Completed,
    Failed,
    Cancelled,
}

public enum GoalStepState
{
    Pending,
    InProgress,
    Completed,
    Failed,
    Skipped,
}

public sealed record GoalStepSnapshot(
    int Id,
    string Description,
    string SuccessCriteria,
    GoalStepState State,
    IReadOnlyList<string> RequiredTools,
    int Depth,
    bool NeedsFurtherDecomposition,
    IReadOnlyList<string> ExpectedFiles,
    IReadOnlyList<string> AllowedPaths,
    bool RequiresBuild,
    bool RequiresTests,
    bool Optional,
    // Fix-5：子目标分解失败回退直执行，或深度上限后未再拆分即执行时置位，供 TUI 卡片与事后审计。
    bool DecompositionFallback = false);

public sealed record GoalStepExecutionEvidence(
    int GoalId,
    GoalStepState State,
    int Attempts,
    long InputTokens,
    long OutputTokens,
    string AgentOutput,
    string Evaluation,
    IReadOnlyList<string> ChangedFiles,
    IReadOnlyList<GoalToolEvidence> ToolExecutions,
    IReadOnlyList<GoalGateEvidence> Validations,
    IReadOnlyList<string> Diagnostics);

public sealed record GoalToolEvidence(string ToolName, bool IsError, string? Result);

public sealed record GoalGateEvidence(string Gate, bool Passed, bool Skipped, string Summary);

public sealed record GoalBudgetSnapshot(
    int TotalAttempts,
    long TotalInputTokens,
    long TotalOutputTokens,
    DateTimeOffset StartedAt,
    // Fix-7：仅累计运行区间的墙钟（Paused/离线时间不计入）。零值且无 LastActivityAt 时回退 UtcNow-StartedAt 兼容旧快照。
    TimeSpan AccumulatedElapsed = default,
    // 上次活动时间戳，用于增量累计 AccumulatedElapsed。
    DateTimeOffset? LastActivityAt = null);

public sealed record GoalStepReceipt(
    string OperationId,
    int GoalId,
    string Commit,
    GoalStepExecutionEvidence Evidence,
    bool Replayed,
    DateTimeOffset RecordedAt);

public sealed record GoalPublishReceipt(
    string OperationId,
    string ResultHash,
    IReadOnlyList<string> ChangedFiles,
    DateTimeOffset PublishedAt,
    bool Replayed = false);

public sealed record GoalWorkspaceSnapshot(
    string WorkspaceId,
    string RepositoryRoot,
    string IsolatedPath,
    string WorktreeBranch,
    string TargetBranch,
    string BaseCommit,
    string TargetWorkspaceFingerprint,
    DateTimeOffset CreatedAt,
    // 创建 worktree 时带入的目标工作区未提交改动数（干净树为 null，便于区分“未知”与“没有”）。
    int? CarriedUncommittedCount = null,
    // 带入的未提交改动路径样本（与 CarriedUncommittedCount 对应，最多展示前若干条）。
    IReadOnlyList<string>? CarriedUncommittedPaths = null);

public sealed record GoalRun : IWorkflowRun
{
    public required GoalRunId Id { get; init; }
    public required SessionId SessionId { get; init; }
    public required string Goal { get; init; }
    public required string WorkingDirectory { get; init; }
    public required string WorkspaceFingerprint { get; init; }
    public required string DefinitionHash { get; init; }
    public GoalRunState State { get; init; } = GoalRunState.Planning;
    public IReadOnlyList<GoalStepSnapshot> Plan { get; init; } = [];
    public IReadOnlyList<GoalStepExecutionEvidence> Executions { get; init; } = [];
    public bool HasReplanned { get; init; }
    public GoalBudgetSnapshot Budget { get; init; } = new(0, 0, 0, DateTimeOffset.UtcNow);
    public IReadOnlyList<GoalGateEvidence> FinalValidation { get; init; } = [];
    public GoalWorkspaceSnapshot? Workspace { get; init; }
    public GoalPublishReceipt? PublishReceipt { get; init; }
    public RunTerminalReason? TerminalReason { get; init; }
    public string? FailureSummary { get; init; }
    public long? WorkflowFencingToken { get; init; }
    public long Version { get; init; }
    public long SequenceNumber { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; init; }

    public bool IsTerminal => State is GoalRunState.Blocked
        or GoalRunState.Completed
        or GoalRunState.Failed
        or GoalRunState.Cancelled;
}
