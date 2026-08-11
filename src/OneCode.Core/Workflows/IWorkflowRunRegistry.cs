namespace OneCode.Core.Workflows;

/// <summary>
/// Persistent source of truth for workflow runtime identity, lease fencing and reconciled checkpoints.
/// A checkpoint file without a corresponding active record is never a recoverable business run.
/// </summary>
public interface IWorkflowRunRegistry
{
    Task<WorkflowRunRecord?> LoadAsync(string runId, CancellationToken ct = default);

    Task<IReadOnlyList<WorkflowRunRecord>> LoadActiveAsync(CancellationToken ct = default);

    Task<IWorkflowRunLease?> TryAcquireAsync(
        WorkflowRunRegistration registration,
        CancellationToken ct = default);

    Task<WorkflowRunRecord> BeginGenerationAsync(
        string runId,
        long fencingToken,
        int generation,
        CancellationToken ct = default);

    Task ReconcileCheckpointAsync(
        string runId,
        long fencingToken,
        string checkpointId,
        CancellationToken ct = default);

    Task RegisterPendingRequestAsync(
        string runId,
        long fencingToken,
        WorkflowPendingRequest request,
        CancellationToken ct = default);

    Task ConsumePendingRequestAsync(
        string runId,
        long fencingToken,
        WorkflowPendingRequest request,
        CancellationToken ct = default);

    Task CompleteAsync(
        string runId,
        long fencingToken,
        WorkflowRunState terminalState,
        CancellationToken ct = default);
}

/// <summary>Exclusive ownership of one workflow run.</summary>
public interface IWorkflowRunLease : IAsyncDisposable
{
    string RunId { get; }
    long FencingToken { get; }
}
