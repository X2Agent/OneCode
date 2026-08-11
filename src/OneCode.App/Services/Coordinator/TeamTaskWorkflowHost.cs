using OneCode.App.Services.Agent;
using OneCode.Core.Coordinator;
using OneCode.Core.Workflows;

namespace OneCode.App.Services.Coordinator;

/// <summary>
/// Runs an approved Team task DAG through the shared <see cref="DurableWorkflowHost"/>.
/// Lease acquisition atomically claims the TeamRun with the same fencing token, binds the
/// per-run runtime, and only then opens the per-run Checkpoint Store. A new execution
/// generation clears the previous generation's checkpoint/pending request so a crashed
/// run restarts from the business aggregate instead of a stale MAF cursor.
/// </summary>
internal sealed class TeamTaskWorkflowHost(
    IDurableWorkflowHost durableHost,
    TeamTaskWorkflowCompiler compiler,
    ITeamRunStore teamRunStore,
    IWorkflowRunRegistry workflowRunRegistry)
{
    public async Task<TeamTaskWorkflowResult> RunNextAsync(
        TeamRun teamRun,
        TeamConfig config,
        string modelId,
        ITeamTaskWorkflowRuntime runtime,
        JsonSerializerOptions serializerOptions,
        Func<WorkflowRuntimeEvent, CancellationToken, ValueTask>? eventSink = null,
        CancellationToken ct = default)
    {
        var record = await workflowRunRegistry.LoadAsync($"team/{teamRun.Id}", ct).ConfigureAwait(false);
        if (record?.IsTerminal == true)
        {
            throw new InvalidOperationException(
                $"Team workflow run 'team/{teamRun.Id}' already reached terminal state '{record.State}'.");
        }
        return await RunAsync(
            teamRun,
            config,
            modelId,
            runtime,
            serializerOptions,
            eventSink,
            executionGeneration: (record?.ExecutionGeneration ?? 0) + 1,
            terminalStateResolver: static _ => (WorkflowRunState?)null,
            ct: ct).ConfigureAwait(false);
    }

    public async Task<TeamTaskWorkflowResult> RunAsync(
        TeamRun teamRun,
        TeamConfig config,
        string modelId,
        ITeamTaskWorkflowRuntime runtime,
        JsonSerializerOptions serializerOptions,
        Func<WorkflowRuntimeEvent, CancellationToken, ValueTask>? eventSink = null,
        int executionGeneration = 1,
        Func<IReadOnlyList<WorkflowRuntimeEvent>, WorkflowRunState?>? terminalStateResolver = null,
        CancellationToken ct = default)
    {
        if (teamRun.PlanApproved is false)
            throw new InvalidOperationException($"TeamRun '{teamRun.Id}' has no approved plan.");
        if (teamRun.Status is not (TeamRunStatus.Running or TeamRunStatus.Blocked))
        {
            throw new InvalidOperationException(
                $"TeamRun '{teamRun.Id}' cannot start a workflow from status '{teamRun.Status}'.");
        }

        var definition = compiler.Compile(
            teamRun,
            config,
            modelId,
            runtime,
            serializerOptions);
        var durable = await durableHost.RunAsync(
            definition.Registration,
            definition.Workflow,
            definition.Input,
            commandId: definition.Input.RunId.ToString(),
            serializerOptions,
            eventSink,
            executionGeneration: executionGeneration,
            terminalStateResolver: terminalStateResolver,
            leaseAcquired: async (workflowRun, callbackCt) =>
            {
                var current = await teamRunStore.LoadAsync(teamRun.Id, callbackCt).ConfigureAwait(false)
                    ?? throw new InvalidOperationException($"TeamRun '{teamRun.Id}' was not found.");
                if (current.Status != TeamRunStatus.Running)
                {
                    throw new InvalidOperationException(
                        $"TeamRun '{teamRun.Id}' cannot start an attempt from status '{current.Status}'.");
                }

                var claimed = await teamRunStore.ClaimWorkflowAsync(
                    teamRun.Id,
                    workflowRun.FencingToken,
                    current.Version,
                    callbackCt).ConfigureAwait(false);
                if (claimed.WorkflowFencingToken != workflowRun.FencingToken)
                    throw new InvalidOperationException("TeamRun and Workflow Registry fencing tokens diverged.");
                await runtime.BindAsync(claimed, workflowRun.FencingToken, callbackCt).ConfigureAwait(false);
            },
            ct: ct).ConfigureAwait(false);

        // 所有任务的结构化结果由共享 outcome registry 记录（包括被上游阻塞的 Blocked 任务）；
        // 终端 Output 事件不足以覆盖中间任务，故从 registry 统一读取。
        var outcomes = definition.OutcomeRegistry.GetAll();
        if (outcomes.Count == 0 && durable.Run.State == WorkflowRunState.Completed)
        {
            throw new InvalidOperationException(
                $"Team workflow run '{definition.Registration.RunId}' completed without any task outcome.");
        }

        return new TeamTaskWorkflowResult(durable, outcomes);
    }

    /// <summary>
    /// Closes the technical workflow only after TeamRun has durably completed its
    /// quality-gate, delivery and business-terminal transition.
    /// </summary>
    public Task CompleteBusinessAsync(
        TeamRunId runId,
        long fencingToken,
        WorkflowRunState state,
        CancellationToken ct = default)
        => workflowRunRegistry.CompleteAsync(
            $"team/{runId}",
            fencingToken,
            state,
            ct);
}
