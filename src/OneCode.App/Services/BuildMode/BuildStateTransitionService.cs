using OneCode.Core.Build;

namespace OneCode.App.Services.BuildMode;

/// <summary>
/// Deterministic transition guard for the BuildRun aggregate.
/// </summary>
public sealed class BuildStateTransitionService
{
    private static readonly IReadOnlyDictionary<BuildRunState, IReadOnlySet<BuildRunState>> s_transitions =
        new Dictionary<BuildRunState, IReadOnlySet<BuildRunState>>
        {
            [BuildRunState.Created] = Set(BuildRunState.Intake, BuildRunState.Cancelled),
            [BuildRunState.Intake] = Set(BuildRunState.Assessing, BuildRunState.Cancelled),
            [BuildRunState.Assessing] = Set(BuildRunState.Clarifying, BuildRunState.ScopeConfirmed, BuildRunState.Blocked, BuildRunState.Cancelled),
            [BuildRunState.Clarifying] = Set(BuildRunState.ScopeConfirmed, BuildRunState.Blocked, BuildRunState.Cancelled),
            [BuildRunState.ScopeConfirmed] = Set(BuildRunState.Planning, BuildRunState.Cancelled),
            [BuildRunState.Planning] = Set(BuildRunState.Planned, BuildRunState.Blocked, BuildRunState.Cancelled),
            [BuildRunState.Planned] = Set(BuildRunState.Implementing, BuildRunState.Blocked, BuildRunState.Cancelled),
            [BuildRunState.Implementing] = Set(BuildRunState.Verifying, BuildRunState.Recovering, BuildRunState.Failed, BuildRunState.Blocked, BuildRunState.Cancelled, BuildRunState.LimitReached, BuildRunState.BudgetExceeded),
            [BuildRunState.Verifying] = Set(BuildRunState.Accepting, BuildRunState.Recovering, BuildRunState.Failed, BuildRunState.Blocked, BuildRunState.Cancelled),
            [BuildRunState.Recovering] = Set(BuildRunState.Implementing, BuildRunState.Verifying, BuildRunState.Failed, BuildRunState.Blocked, BuildRunState.Cancelled),
            [BuildRunState.Accepting] = Set(BuildRunState.Completed, BuildRunState.Failed, BuildRunState.Blocked, BuildRunState.Cancelled),
        };

    public BuildRun Transition(BuildRun current, BuildRunState target, DateTimeOffset occurredAt)
    {
        if (current.State == target)
            return current;

        if (!s_transitions.TryGetValue(current.State, out var allowed) || !allowed.Contains(target))
        {
            throw new InvalidOperationException(
                $"Illegal BuildRun transition: {current.State} -> {target} for run {current.Id}.");
        }

        var next = current with
        {
            State = target,
            SequenceNumber = current.SequenceNumber + 1,
            UpdatedAt = occurredAt,
        };

        if (target == BuildRunState.Completed)
            ValidateCompletionInvariant(next);

        return next;
    }

    public static bool IsTerminal(BuildRunState state) => state is
        BuildRunState.Completed
        or BuildRunState.Failed
        or BuildRunState.Blocked
        or BuildRunState.Cancelled
        or BuildRunState.LimitReached
        or BuildRunState.BudgetExceeded;

    public static void ValidateCompletionInvariant(BuildRun run)
    {
        if (run.Scope is null)
            throw new InvalidOperationException("A completed BuildRun requires a confirmed scope snapshot.");
        if (run.Plan is null)
            throw new InvalidOperationException("A completed BuildRun requires a structured plan.");
        var executableTasks = run.Plan.Tasks
            .Where(task => task.Status != BuildTaskStatus.Skipped)
            .ToArray();
        if (executableTasks.Any(task => task.Status != BuildTaskStatus.Completed))
            throw new InvalidOperationException("All required Build tasks must be completed.");
        if (executableTasks.Any(task => task.CompletionEvidence.Count == 0))
            throw new InvalidOperationException("Every completed Build task requires implementation or validation evidence.");
        if (executableTasks.Any(task => string.IsNullOrWhiteSpace(task.TaskItemId)))
            throw new InvalidOperationException("Every completed Build task requires a persistent TaskItem mapping.");
        if (executableTasks.Any(task => !task.CompletionEvidence.Any(evidence =>
            evidence.Kind == BuildEvidenceKind.TaskCompletion
            && string.Equals(evidence.Reference, task.TaskItemId, StringComparison.Ordinal))))
        {
            throw new InvalidOperationException(
                "Every completed Build task requires TaskCompletion evidence referencing its persistent TaskItem.");
        }
        if (run.Validations.Count == 0 || run.Validations[^1].Status != BuildValidationStatus.Passed)
            throw new InvalidOperationException("The latest final validation must pass.");
        if (run.Scope.AcceptanceCriteria.Where(item => item.Required)
            .Any(item => item.Status != AcceptanceStatus.Passed || string.IsNullOrWhiteSpace(item.Evidence)))
        {
            throw new InvalidOperationException("All required acceptance criteria need passing evidence.");
        }
        if (run.ChangedFiles.Count > 0 && !run.TransactionCommitted)
            throw new InvalidOperationException("A BuildRun with file changes must commit its transaction before completion.");
        if (run.TransactionRolledBack)
            throw new InvalidOperationException("A rolled-back BuildRun cannot complete successfully.");
        if (run.DeliveryManifest is null)
            throw new InvalidOperationException("A completed BuildRun requires a delivery manifest.");
        if (run.TerminalReason != BuildTerminalReason.Completed)
            throw new InvalidOperationException("A completed BuildRun requires the Completed terminal reason.");
    }

    private static IReadOnlySet<BuildRunState> Set(params BuildRunState[] states) =>
        new HashSet<BuildRunState>(states);
}
