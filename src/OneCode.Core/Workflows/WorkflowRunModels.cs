namespace OneCode.Core.Workflows;

/// <summary>Durable lifecycle of a MAF workflow run.</summary>
public enum WorkflowRunState
{
    Active,
    Completed,
    Failed,
    Cancelled,
}

/// <summary>Stable inputs required to create or resume a workflow run.</summary>
public sealed record WorkflowRunRegistration(
    string RunId,
    string RunKind,
    string DefinitionHash);

/// <summary>Durable identity and routing metadata for a pending MAF request.</summary>
public sealed record WorkflowPendingRequest(
    string RequestId,
    string PortId,
    string CommandId,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ExpiresAt = null);

/// <summary>
/// Durable workflow runtime metadata. Business stores remain authoritative for product state;
/// this record owns only runtime identity, fencing and the last reconciled checkpoint.
/// </summary>
public sealed record WorkflowRunRecord(
    string RunId,
    string RunKind,
    string DefinitionHash,
    long FencingToken,
    WorkflowRunState State,
    string? CheckpointId,
    long Version,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? CompletedAt = null,
    WorkflowPendingRequest? PendingRequest = null,
    int ExecutionGeneration = 0)
{
    public bool IsTerminal => State is WorkflowRunState.Completed
        or WorkflowRunState.Failed
        or WorkflowRunState.Cancelled;
}
