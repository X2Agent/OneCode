using Microsoft.Agents.AI.Workflows;
using OneCode.App.Services.Agent;
using OneCode.Core.Coordinator;
using OneCode.Core.Workflows;

namespace OneCode.App.Services.Coordinator;

/// <summary>
/// Runs the TeamRun plan-approval gate through the shared <see cref="DurableWorkflowHost"/>.
/// The first invocation suspends on the MAF RequestPort (IsPending); the caller surfaces the
/// pending RequestId/PortId so the user can approve. After <see cref="IWorkflowRequestAdapter.SendResponseAsync"/>
/// consumed the response, invoking <c>RunApprovalAsync</c> again resumes on the SAME execution
/// generation (no new generation) and drains to the single terminal decision.
/// </summary>
internal sealed class TeamApprovalWorkflowHost(
    IDurableWorkflowHost durableHost,
    TeamApprovalWorkflowCompiler compiler,
    IWorkflowRunRegistry workflowRunRegistry)
{
    public async Task<TeamApprovalWorkflowResult> RunApprovalAsync(
        string teamName,
        TeamRunId runId,
        TeamConfig config,
        string modelId,
        TeamPlanApprovalInput input,
        JsonSerializerOptions serializerOptions,
        Func<WorkflowRuntimeEvent, CancellationToken, ValueTask>? eventSink = null,
        ExternalResponse? externalResponse = null,
        CancellationToken ct = default)
    {
        var definition = compiler.Compile(teamName, runId, config, modelId, input);
        var registryRunId = definition.Registration.RunId;

        var record = await workflowRunRegistry.LoadAsync(registryRunId, ct).ConfigureAwait(false);
        if (record?.IsTerminal == true)
        {
            throw new InvalidOperationException(
                $"Plan approval run '{registryRunId}' already reached terminal state '{record.State}'.");
        }

        // 首次调用开启第 1 个执行世代；响应续跑时保留当前世代（不清 checkpoint / pending）。
        int? executionGeneration = record is null ? 1 : null;

        var durable = await durableHost.RunAsync(
            definition.Registration,
            definition.Workflow,
            definition.Input,
            commandId: $"team/{runId}/approve",
            serializerOptions,
            eventSink,
            executionGeneration,
            externalResponse: externalResponse,
            ct: ct).ConfigureAwait(false);

        var pending = durable.Events
            .OfType<WorkflowRuntimeEvent.PendingRequest>()
            .Select(item => item.Request)
            .LastOrDefault();

        bool? granted = null;
        if (!durable.IsPending)
        {
            granted = durable.Events
                .OfType<WorkflowRuntimeEvent.Output>()
                .Select(item => item.Value)
                .OfType<TeamPlanApprovalOutput>()
                .FirstOrDefault()
                ?.Approved;
        }

        return new TeamApprovalWorkflowResult(durable, pending, granted);
    }
}