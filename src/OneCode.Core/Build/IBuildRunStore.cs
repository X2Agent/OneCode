using OneCode.Core.Domain;
using OneCode.Core.Workflows;

namespace OneCode.Core.Build;

/// <summary>
/// Persistence interface for <see cref="BuildRun"/> aggregates.
/// Implementations must support optimistic concurrency via the expectedVersion parameter.
/// CAS / fencing 方法签名由 <see cref="IWorkflowRunStore{TRun,TId}"/> 内核收编。
/// </summary>
public interface IBuildRunStore : IWorkflowRunStore<BuildRun, BuildRunId>
{
    Task<BuildRun?> LoadAsync(SessionId? conversationId, CancellationToken ct = default);

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
