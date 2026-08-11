using Microsoft.Agents.AI.Workflows;
using OneCode.Core.Workflows;

namespace OneCode.App.Services.Agent;

public interface IWorkflowEventAdapter
{
    WorkflowRuntimeEvent? Adapt(string runId, WorkflowEvent workflowEvent, string commandId);
}

/// <summary>Single projection point from MAF events into stable OneCode runtime events.</summary>
public sealed class WorkflowEventAdapter : IWorkflowEventAdapter
{
    public WorkflowRuntimeEvent? Adapt(string runId, WorkflowEvent workflowEvent, string commandId)
    {
        if (string.IsNullOrWhiteSpace(runId))
            throw new ArgumentException("RunId is required.", nameof(runId));
        ArgumentNullException.ThrowIfNull(workflowEvent);

        return workflowEvent switch
        {
            RequestInfoEvent request => new WorkflowRuntimeEvent.PendingRequest(
                runId,
                ExecutorId: null,
                new WorkflowPendingRequest(
                    request.Request.RequestId,
                    request.Request.PortInfo.PortId,
                    commandId,
                    DateTimeOffset.UtcNow)),
            WorkflowOutputEvent output => new WorkflowRuntimeEvent.Output(runId, output.ExecutorId, output.Data),
            ExecutorFailedEvent failed => new WorkflowRuntimeEvent.Failed(
                runId,
                failed.ExecutorId,
                failed.Data?.ToString() ?? failed.ToString()),
            WorkflowErrorEvent failed => new WorkflowRuntimeEvent.Failed(
                runId,
                ExecutorId: null,
                failed.Exception?.Message ?? failed.ToString()),
            _ => null,
        };
    }
}

public interface IWorkflowRequestAdapter
{
    Task RegisterAsync(
        string runId,
        long fencingToken,
        WorkflowPendingRequest request,
        CancellationToken ct = default);

    Task SendResponseAsync(
        StreamingRun run,
        string runId,
        long fencingToken,
        WorkflowPendingRequest request,
        ExternalResponse response,
        CancellationToken ct = default);
}

/// <summary>
/// Durable validation boundary for MAF HITL responses. UI layers persist and route request identity;
/// they do not own an in-memory TaskCompletionSource for durable workflow runs.
/// </summary>
public sealed class WorkflowRequestAdapter(IWorkflowRunRegistry registry) : IWorkflowRequestAdapter
{
    public Task RegisterAsync(
        string runId,
        long fencingToken,
        WorkflowPendingRequest request,
        CancellationToken ct = default)
        => registry.RegisterPendingRequestAsync(runId, fencingToken, request, ct);

    public async Task SendResponseAsync(
        StreamingRun run,
        string runId,
        long fencingToken,
        WorkflowPendingRequest request,
        ExternalResponse response,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(response);
        ct.ThrowIfCancellationRequested();

        if (!string.Equals(response.RequestId, request.RequestId, StringComparison.Ordinal)
            || !string.Equals(response.PortInfo.PortId, request.PortId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Workflow response identity does not match the durable pending request.");
        }

        var record = await registry.LoadAsync(runId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Workflow run '{runId}' does not exist.");
        if (record.FencingToken != fencingToken || record.State != WorkflowRunState.Active)
            throw new InvalidOperationException("Stale workflow fencing token.");
        if (record.PendingRequest is not { } pending
            || !string.Equals(pending.RequestId, request.RequestId, StringComparison.Ordinal)
            || !string.Equals(pending.PortId, request.PortId, StringComparison.Ordinal)
            || !string.Equals(pending.CommandId, request.CommandId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Pending workflow request identity does not match.");
        }
        if (request.ExpiresAt is { } expiresAt && expiresAt <= DateTimeOffset.UtcNow)
            throw new InvalidOperationException("Pending workflow request has expired.");

        await run.SendResponseAsync(response).ConfigureAwait(false);
        await registry.ConsumePendingRequestAsync(runId, fencingToken, request, ct).ConfigureAwait(false);
    }
}
