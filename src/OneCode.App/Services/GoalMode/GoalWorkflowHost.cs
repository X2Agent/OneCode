using OneCode.App.Services.Agent;
using OneCode.Core.Goals;
using OneCode.Core.Workflows;

namespace OneCode.App.Services.GoalMode;

public sealed record GoalWorkflowRunResult(
    DurableWorkflowRunResult Durable,
    GoalWorkflowOutput Output);

public sealed class GoalWorkflowHost(
    IDurableWorkflowHost durableHost,
    GoalWorkflowCompiler compiler,
    IGoalRunStore goalRunStore,
    IWorkflowRunRegistry workflowRunRegistry)
{
    public async Task<GoalWorkflowRunResult> RunNextAsync(
        GoalRun goalRun,
        string modelId,
        string systemPromptHash,
        string toolCapabilityHash,
        IGoalWorkflowRuntime runtime,
        JsonSerializerOptions serializerOptions,
        Func<WorkflowRuntimeEvent, CancellationToken, ValueTask>? eventSink = null,
        CancellationToken ct = default)
    {
        var existing = await workflowRunRegistry.LoadAsync($"goal/{goalRun.Id}", ct).ConfigureAwait(false);
        var generation = (existing?.ExecutionGeneration ?? 0) + 1;
        return await RunAsync(
            goalRun,
            modelId,
            systemPromptHash,
            toolCapabilityHash,
            runtime,
            serializerOptions,
            eventSink,
            generation,
            ct).ConfigureAwait(false);
    }

    public async Task<GoalWorkflowRunResult> RunAsync(
        GoalRun goalRun,
        string modelId,
        string systemPromptHash,
        string toolCapabilityHash,
        IGoalWorkflowRuntime runtime,
        JsonSerializerOptions serializerOptions,
        Func<WorkflowRuntimeEvent, CancellationToken, ValueTask>? eventSink = null,
        int executionGeneration = 1,
        CancellationToken ct = default)
    {
        var definition = compiler.Compile(
            goalRun,
            modelId,
            systemPromptHash,
            toolCapabilityHash,
            runtime,
            serializerOptions);
        var durable = await durableHost.RunAsync(
            definition.Registration,
            definition.Workflow,
            definition.Input,
            $"goal/{goalRun.Id}/execute",
            serializerOptions,
            eventSink,
            executionGeneration,
            leaseAcquired: async (workflowRun, callbackCt) =>
            {
                var current = await goalRunStore.LoadByIdAsync(goalRun.Id, callbackCt).ConfigureAwait(false)
                    ?? throw new InvalidOperationException($"GoalRun '{goalRun.Id}' was not found.");
                if (current.IsTerminal)
                    throw new InvalidOperationException($"Terminal GoalRun '{goalRun.Id}' cannot acquire workflow execution.");
                var claimed = await goalRunStore.ClaimWorkflowAsync(
                    goalRun.Id,
                    workflowRun.FencingToken,
                    current.Version,
                    callbackCt).ConfigureAwait(false);
                if (claimed.WorkflowFencingToken != workflowRun.FencingToken)
                    throw new InvalidOperationException("GoalRun and Workflow Registry fencing tokens diverged.");
                await runtime.BindAsync(claimed, workflowRun.FencingToken, callbackCt).ConfigureAwait(false);
            },
            terminalStateResolver: events => ResolveTerminalState(events),
            ct: ct).ConfigureAwait(false);
        var output = durable.Events
            .OfType<WorkflowRuntimeEvent.Output>()
            .Select(item => item.Value)
            .OfType<GoalWorkflowOutput>()
            .SingleOrDefault()
            ?? throw new InvalidOperationException($"Goal workflow '{definition.Registration.RunId}' produced no typed output.");
        return new GoalWorkflowRunResult(durable, output);
    }

    private static WorkflowRunState? ResolveTerminalState(IReadOnlyList<WorkflowRuntimeEvent> events)
    {
        var output = events
            .OfType<WorkflowRuntimeEvent.Output>()
            .Select(item => item.Value)
            .OfType<GoalWorkflowOutput>()
            .SingleOrDefault();
        return output?.State switch
        {
            GoalRunState.Paused => null,
            GoalRunState.Completed => WorkflowRunState.Completed,
            GoalRunState.Cancelled => WorkflowRunState.Cancelled,
            GoalRunState.Blocked or GoalRunState.Failed => WorkflowRunState.Failed,
            GoalRunState.Validating or GoalRunState.Publishing or GoalRunState.Executing or GoalRunState.Planning
                => WorkflowRunState.Failed,
            null => WorkflowRunState.Failed,
            _ => WorkflowRunState.Failed,
        };
    }
}
