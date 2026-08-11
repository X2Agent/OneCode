using OneCode.Core.Build;
using OneCode.Core.Domain;

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
    bool Optional);

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
    decimal EstimatedCostUsd,
    DateTimeOffset StartedAt);

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
    DateTimeOffset CreatedAt);

public sealed record GoalRun
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
    public GoalBudgetSnapshot Budget { get; init; } = new(0, 0, 0, 0m, DateTimeOffset.UtcNow);
    public IReadOnlyList<GoalGateEvidence> FinalValidation { get; init; } = [];
    public GoalWorkspaceSnapshot? Workspace { get; init; }
    public GoalPublishReceipt? PublishReceipt { get; init; }
    public BuildTerminalReason? TerminalReason { get; init; }
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
