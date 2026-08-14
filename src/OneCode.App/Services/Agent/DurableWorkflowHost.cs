using Microsoft.Agents.AI.Workflows;
using OneCode.Core.Workflows;

namespace OneCode.App.Services.Agent;

public sealed record DurableWorkflowRunResult(
    WorkflowRunRecord Run,
    bool Resumed,
    bool IsPending,
    IReadOnlyList<WorkflowRuntimeEvent> Events);

public interface IDurableWorkflowHost
{
    Task<DurableWorkflowRunResult> RunAsync<TInput>(
        WorkflowRunRegistration registration,
        Workflow workflow,
        TInput input,
        string commandId,
        JsonSerializerOptions serializerOptions,
        Func<WorkflowRuntimeEvent, CancellationToken, ValueTask>? eventSink = null,
        int? executionGeneration = null,
        Func<WorkflowRunRecord, CancellationToken, ValueTask>? leaseAcquired = null,
        Func<IReadOnlyList<WorkflowRuntimeEvent>, WorkflowRunState?>? terminalStateResolver = null,
        ExternalResponse? externalResponse = null,
        CancellationToken ct = default);
}

/// <summary>
/// Single durable execution host for persistent MAF workflows. It owns the Registry lease,
/// process-exclusive checkpoint store, checkpoint reconciliation and runtime event projection.
/// </summary>
public sealed class DurableWorkflowHost(
    IWorkflowRunRegistry registry,
    IWorkflowCheckpointStoreFactory checkpointFactory,
    IWorkflowEventAdapter eventAdapter,
    ILogger<DurableWorkflowHost> logger,
    IWorkflowRequestAdapter? requestAdapter = null) : IDurableWorkflowHost
{
    public async Task<DurableWorkflowRunResult> RunAsync<TInput>(
        WorkflowRunRegistration registration,
        Workflow workflow,
        TInput input,
        string commandId,
        JsonSerializerOptions serializerOptions,
        Func<WorkflowRuntimeEvent, CancellationToken, ValueTask>? eventSink = null,
        int? executionGeneration = null,
        Func<WorkflowRunRecord, CancellationToken, ValueTask>? leaseAcquired = null,
        Func<IReadOnlyList<WorkflowRuntimeEvent>, WorkflowRunState?>? terminalStateResolver = null,
        ExternalResponse? externalResponse = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(registration);
        ArgumentNullException.ThrowIfNull(workflow);
        ArgumentNullException.ThrowIfNull(serializerOptions);
        if (string.IsNullOrWhiteSpace(commandId))
            throw new ArgumentException("CommandId is required.", nameof(commandId));

        await using var lease = await registry.TryAcquireAsync(registration, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Workflow run '{registration.RunId}' lease is already held.");
        var durable = await registry.LoadAsync(registration.RunId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Workflow run '{registration.RunId}' was not persisted.");
        if (durable.FencingToken != lease.FencingToken)
            throw new InvalidOperationException("Workflow registry returned a stale fencing token.");
        if (executionGeneration is { } generation)
        {
            durable = await registry.BeginGenerationAsync(
                registration.RunId,
                lease.FencingToken,
                generation,
                ct).ConfigureAwait(false);
        }
        if (leaseAcquired is not null)
            await leaseAcquired(durable, ct).ConfigureAwait(false);

        await using var checkpointHandle = await checkpointFactory
            .OpenAsync(durable, serializerOptions, ct).ConfigureAwait(false);
        var environment = InProcessExecution.Lockstep.WithCheckpointing(checkpointHandle.Manager);
        var resumed = !string.IsNullOrWhiteSpace(durable.CheckpointId);
        await using var run = resumed
            ? await environment.ResumeStreamingAsync(
                workflow,
                new CheckpointInfo(registration.RunId, durable.CheckpointId!),
                ct).ConfigureAwait(false)
            : await environment.RunStreamingAsync(workflow, input, registration.RunId, ct).ConfigureAwait(false);

        List<WorkflowRuntimeEvent> events = [];
        var pending = false;
        var failed = false;
        var requestAdapterInstance = requestAdapter ?? new WorkflowRequestAdapter(registry);
        try
        {
            while (true)
            {
                pending = false;
                WorkflowPendingRequest? pendingAtLoopStart = null;
                await foreach (var workflowEvent in run.WatchStreamAsync(
                                   blockOnPendingRequest: false,
                                   ct).ConfigureAwait(false))
                {
                    var adapted = eventAdapter.Adapt(registration.RunId, workflowEvent, commandId);
                    if (adapted is null)
                        continue;
                    events.Add(adapted);
                    if (adapted is WorkflowRuntimeEvent.PendingRequest request)
                    {
                        pending = true;
                        pendingAtLoopStart = request.Request;
                        await registry.RegisterPendingRequestAsync(
                            registration.RunId,
                            lease.FencingToken,
                            request.Request,
                            ct).ConfigureAwait(false);
                    }
                    else if (adapted is WorkflowRuntimeEvent.Failed)
                    {
                        failed = true;
                    }

                    if (eventSink is not null)
                        await eventSink(adapted, ct).ConfigureAwait(false);
                }

                // 挂起且调用方提供了外部响应：投递响应后在本执行世代内继续 drain（到下一挂起/终态）。
                if (pending && pendingAtLoopStart is not null && externalResponse is not null)
                {
                    await requestAdapterInstance.SendResponseAsync(
                        run,
                        registration.RunId,
                        lease.FencingToken,
                        pendingAtLoopStart,
                        externalResponse,
                        ct).ConfigureAwait(false);
                    continue;
                }
                break;
            }

            if (run.LastCheckpoint is { } checkpoint)
            {
                await registry.ReconcileCheckpointAsync(
                    registration.RunId,
                    lease.FencingToken,
                    checkpoint.CheckpointId,
                    ct).ConfigureAwait(false);
            }

            if (!pending)
            {
                var terminalState = terminalStateResolver?.Invoke(events)
                    ?? (terminalStateResolver is null
                        ? failed ? WorkflowRunState.Failed : WorkflowRunState.Completed
                        : null);
                if (terminalState is { } state)
                {
                    await registry.CompleteAsync(
                        registration.RunId,
                        lease.FencingToken,
                        state,
                        ct).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Cancellation is an interruption, not a durable business terminal state.
            // Keep the registry Active so a later process can reacquire the run and resume
            // from the last reconciled checkpoint.
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Workflow run '{RunId}' failed with unhandled exception", registration.RunId);
            await registry.CompleteAsync(
                registration.RunId,
                lease.FencingToken,
                WorkflowRunState.Failed,
                CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        var final = await registry.LoadAsync(registration.RunId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Workflow run '{registration.RunId}' disappeared.");
        return new DurableWorkflowRunResult(final, resumed, pending, events);
    }
}
