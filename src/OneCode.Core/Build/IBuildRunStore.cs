using OneCode.Core.Domain;

namespace OneCode.Core.Build;

/// <summary>
/// Persistence interface for <see cref="BuildRun"/> aggregates.
/// Implementations must support optimistic concurrency via the expectedVersion parameter.
/// </summary>
public interface IBuildRunStore
{
    Task<BuildRun?> LoadAsync(SessionId? conversationId, CancellationToken ct = default);

    Task SaveAsync(BuildRun run, long expectedVersion, CancellationToken ct = default);

    Task<BuildRun> ClaimWorkflowAsync(
        BuildRunId runId,
        long fencingToken,
        long expectedVersion,
        CancellationToken ct = default);

    Task SaveFencedAsync(
        BuildRun run,
        long expectedVersion,
        long fencingToken,
        CancellationToken ct = default);

    Task<BuildRun?> LoadByIdAsync(BuildRunId id, CancellationToken ct = default);
}

/// <summary>
/// Durable BuildRun event sequence used for deterministic replay and audit.
/// </summary>
public interface IBuildRunEventStore
{
    Task<IReadOnlyList<BuildRunEvent>> LoadEventsAsync(
        BuildRunId runId,
        CancellationToken ct = default);

    Task<BuildRun?> ReplayAsync(
        BuildRunId runId,
        CancellationToken ct = default);
}

public interface IWorkspaceFingerprintProvider
{
    Task<string> ComputeAsync(string workingDirectory, CancellationToken ct = default);
}
