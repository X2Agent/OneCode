namespace OneCode.Core.Workflows;

/// <summary>Stable OneCode projection of MAF runtime events.</summary>
public abstract record WorkflowRuntimeEvent(string RunId, string? ExecutorId)
{
    public sealed record Output(string RunId, string? ExecutorId, object? Value)
        : WorkflowRuntimeEvent(RunId, ExecutorId);

    public sealed record Failed(string RunId, string? ExecutorId, string Error)
        : WorkflowRuntimeEvent(RunId, ExecutorId);

    public sealed record PendingRequest(
        string RunId,
        string? ExecutorId,
        WorkflowPendingRequest Request)
        : WorkflowRuntimeEvent(RunId, ExecutorId);
}
