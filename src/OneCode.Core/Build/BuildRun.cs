using OneCode.Core.Domain;
using OneCode.Core.Tools;

namespace OneCode.Core.Build;

/// <summary>
/// BuildRun aggregate root — tracks the lifecycle of a single Build mode execution.
/// Owned by <c>BuildRunCoordinator</c>, not by ChatService or MainAgentRunner.
/// </summary>
public sealed record BuildRun
{
    public required BuildRunId Id { get; init; }
    public required SessionId? ConversationId { get; init; }
    public required BuildRunState State { get; init; }
    public string IntakePrompt { get; init; } = string.Empty;
    public BuildScopeSnapshot? ProposedScope { get; init; }
    public BuildScopeSnapshot? Scope { get; init; }
    public BuildPlan? Plan { get; init; }
    public RequirementAssessment? Assessment { get; init; }
    public IReadOnlyList<string> ClarificationQuestions { get; init; } = [];
    /// <summary>Approved tool policy confirmed by the user at the plan gate. Null until the plan is approved.</summary>
    public ApprovedToolPolicy? ApprovedToolPolicy { get; init; }
    public DateTimeOffset? PlanApprovedAt { get; init; }
    /// <summary>Provenance of the plan approval ("prescribed-plan" | "runtime-approved").</summary>
    public string? PlanApprovalSource { get; init; }
    public string? PlanRejectionReason { get; init; }
    public IReadOnlyList<BuildValidationRun> Validations { get; init; } = [];
    public IReadOnlyList<string> ChangedFiles { get; init; } = [];
    public IReadOnlyList<CompletedToolBatch> ToolBatches { get; init; } = [];
    public BuildDeliveryManifest? DeliveryManifest { get; init; }
    public string? WorkingDirectory { get; init; }
    public string? WorkspaceFingerprint { get; init; }
    public string? CommitWorkspaceFingerprint { get; init; }
    public BuildRunMetrics Metrics { get; init; } = BuildRunMetrics.Empty;
    public BuildTerminalReason? TerminalReason { get; init; }
    public string? FailureSummary { get; init; }
    public bool TransactionCommitted { get; init; }
    public bool TransactionRolledBack { get; init; }
    /// <summary>
    /// Current durable Workflow lease token. Null means controlled execution has not claimed
    /// this BuildRun. Once claimed, all subsequent product-state writes must be fenced.
    /// </summary>
    public long? WorkflowFencingToken { get; init; }
    public long SequenceNumber { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
    public long Version { get; init; }
}

public sealed record BuildRunEvent(
    string EventId,
    BuildRunId RunId,
    SessionId ConversationId,
    long Version,
    long SequenceNumber,
    BuildRunState? FromState,
    BuildRunState ToState,
    DateTimeOffset OccurredAt,
    BuildRun Snapshot);

public readonly record struct BuildRunId(string Value)
{
    public static BuildRunId New() => new($"br-{Guid.NewGuid():N}");
    public override string ToString() => Value;
}

/// <summary>
/// Build run lifecycle states.
/// </summary>
public enum BuildRunState
{
    Created,
    Intake,
    Assessing,
    Clarifying,
    ScopeConfirmed,
    Planning,
    Planned,
    Implementing,
    Verifying,
    Recovering,
    Accepting,
    Completed,
    Failed,
    Blocked,
    Cancelled,
    LimitReached,
    BudgetExceeded,
}

/// <summary>
/// Snapshot of the approved tool policy for a controlled Build run. Confirmed by the user at the
/// plan gate; attempt execution grants these tools only and denies everything else (fail-closed).
/// </summary>
public sealed record ApprovedToolPolicy(IReadOnlyList<string> ToolNames);

/// <summary>
/// Snapshot of a confirmed requirement scope. Immutable after confirmation.
/// </summary>
public sealed record BuildScopeSnapshot(
    string Goal,
    IReadOnlyList<string> InScope,
    IReadOnlyList<string> OutOfScope,
    IReadOnlyList<string> Constraints,
    IReadOnlyList<AcceptanceCriterion> AcceptanceCriteria,
    string ConfirmedBy,
    DateTimeOffset ConfirmedAt);

/// <summary>
/// A single acceptance criterion for a Build run.
/// </summary>
public sealed record AcceptanceCriterion(
    string Id,
    string Description,
    bool Required,
    AcceptanceStatus Status = AcceptanceStatus.Pending,
    string? Evidence = null);

public enum AcceptanceStatus
{
    Pending,
    Passed,
    Failed,
    Skipped,
}

/// <summary>
/// Deterministic assessment performed before Build mode can expose write tools.
/// </summary>
public sealed record RequirementAssessment(
    bool GoalIsClear,
    bool ScopeIsBounded,
    bool AcceptanceIsDeterministic,
    bool ConstraintsAreComplete,
    bool RequiresUserDecision,
    BuildRiskLevel Risk,
    IReadOnlyList<string> Reasons)
{
    public bool RequiresClarification =>
        !GoalIsClear
        || !ScopeIsBounded
        || !AcceptanceIsDeterministic
        || !ConstraintsAreComplete
        || RequiresUserDecision;
}

public enum BuildRiskLevel
{
    Low,
    Medium,
    High,
}

/// <summary>
/// A persisted final-validation attempt. Skipped is intentionally not Passed.
/// </summary>
public sealed record BuildValidationRun(
    string Id,
    BuildValidationStatus Status,
    IReadOnlyList<string> Commands,
    IReadOnlyList<string> Evidence,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt);

public enum BuildValidationStatus
{
    Pending,
    Passed,
    Failed,
    Skipped,
    Cancelled,
}

/// <summary>
/// Structured plan produced during the Planning phase.
/// </summary>
public sealed record BuildPlan(
    string Summary,
    IReadOnlyList<BuildPlanTask> Tasks,
    IReadOnlyList<string> ValidationCommands,
    IReadOnlyList<string> Risks,
    IReadOnlyList<string> NonGoals,
    bool RequireExplicitTaskCompletion = false);

/// <summary>
/// A task within a BuildPlan.
/// </summary>
public sealed record BuildPlanTask(
    string Id,
    string Title,
    string Description,
    IReadOnlyList<string> DependsOn,
    IReadOnlyList<string> ExpectedFiles,
    IReadOnlyList<string> AcceptanceCriteria,
    BuildTaskStatus Status = BuildTaskStatus.Pending,
    IReadOnlyList<BuildTaskEvidence>? Evidence = null,
    string? TaskItemId = null)
{
    public IReadOnlyList<BuildTaskEvidence> CompletionEvidence => Evidence ?? [];
}

public sealed record BuildTaskEvidence(
    BuildEvidenceKind Kind,
    string Reference,
    string Summary);

public enum BuildEvidenceKind
{
    TaskCompletion,
    ToolCall,
    FileChange,
    Validation,
    Acceptance,
}

public sealed record BuildDeliveryManifest(
    IReadOnlyList<string> ChangedFiles,
    IReadOnlyList<string> CompletedTaskIds,
    IReadOnlyList<string> ValidationEvidence,
    IReadOnlyList<string> AcceptanceEvidence,
    IReadOnlyList<string> KnownRisks,
    DateTimeOffset CreatedAt);

public enum BuildTaskStatus
{
    Pending,
    InProgress,
    Completed,
    Failed,
    Skipped,
}

/// <summary>
/// Metrics for a Build run.
/// </summary>
public sealed record BuildRunMetrics(
    int TurnsCompleted,
    int ToolCalls,
    long InputTokens,
    long OutputTokens,
    decimal? EstimatedCost,
    TimeSpan? Duration)
{
    public static readonly BuildRunMetrics Empty = new(0, 0, 0, 0, null, null);
}

/// <summary>
/// Structured result of a completed Build run.
/// </summary>
public sealed record BuildRunResult(
    BuildRunId RunId,
    BuildRunState State,
    BuildTerminalReason TerminalReason,
    string? Summary,
    IReadOnlyList<string> ChangedFiles,
    IReadOnlyList<BuildPlanTask> Tasks,
    IReadOnlyList<BuildValidationRun> Validations,
    IReadOnlyList<AcceptanceCriterion> Acceptance,
    IReadOnlyList<string> KnownRisks,
    BuildDeliveryManifest? DeliveryManifest,
    bool TransactionCommitted,
    bool TransactionRolledBack,
    BuildRunMetrics Metrics);
