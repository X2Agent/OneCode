namespace OneCode.Core.Goals;

public interface IGoalRunStore
{
    Task<GoalRun?> LoadBySessionAsync(
        Domain.SessionId sessionId,
        CancellationToken ct = default);

    Task<GoalRun?> LoadByIdAsync(
        GoalRunId runId,
        CancellationToken ct = default);

    Task SaveAsync(
        GoalRun run,
        long expectedVersion,
        CancellationToken ct = default);

    Task<GoalRun> ClaimWorkflowAsync(
        GoalRunId runId,
        long fencingToken,
        long expectedVersion,
        CancellationToken ct = default);

    Task SaveFencedAsync(
        GoalRun run,
        long expectedVersion,
        long fencingToken,
        CancellationToken ct = default);

    Task<IReadOnlyList<GoalRun>> ListActiveAsync(CancellationToken ct = default);
}
