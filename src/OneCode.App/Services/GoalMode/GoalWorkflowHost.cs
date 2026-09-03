using OneCode.App.Services.Agent;
using OneCode.App.Services.Runtime;
using OneCode.Core.Goals;

namespace OneCode.App.Services.GoalMode;

public sealed record GoalWorkflowRunResult(
    DurableWorkflowRunResult Durable,
    GoalWorkflowOutput Output);

/// <summary>
/// Goal 模式 Workflow Host。执行骨架（generation 递增 / durable 执行 / typed 输出抽取）
/// 由 <see cref="ModeWorkflowHost"/> 统一承担；本类保留 Goal 语义：
/// 工作流编译、lease 回调（终态校验 → claim → 令牌对账 → Bind）与终态解析。
/// </summary>
public sealed class GoalWorkflowHost(
    IDurableWorkflowHost durableHost,
    GoalWorkflowCompiler compiler,
    IGoalRunStore goalRunStore,
    IWorkflowRunRegistry workflowRunRegistry)
{
    private readonly ModeWorkflowHost _modeHost = new(durableHost, workflowRunRegistry);

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
        var result = await _modeHost.RunNextAsync<GoalWorkflowInput, GoalWorkflowOutput>(
            CreatePolicy(goalRun, modelId, systemPromptHash, toolCapabilityHash, runtime, serializerOptions, eventSink),
            ct).ConfigureAwait(false);
        return new GoalWorkflowRunResult(result.Durable, result.Output);
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
        var result = await _modeHost.RunAsync<GoalWorkflowInput, GoalWorkflowOutput>(
            CreatePolicy(goalRun, modelId, systemPromptHash, toolCapabilityHash, runtime, serializerOptions, eventSink),
            executionGeneration,
            ct).ConfigureAwait(false);
        return new GoalWorkflowRunResult(result.Durable, result.Output);
    }

    private ModeWorkflowPolicy<GoalWorkflowInput> CreatePolicy(
        GoalRun goalRun,
        string modelId,
        string systemPromptHash,
        string toolCapabilityHash,
        IGoalWorkflowRuntime runtime,
        JsonSerializerOptions serializerOptions,
        Func<WorkflowRuntimeEvent, CancellationToken, ValueTask>? eventSink)
    {
        var definition = compiler.Compile(
            goalRun,
            modelId,
            systemPromptHash,
            toolCapabilityHash,
            runtime,
            serializerOptions);
        return new ModeWorkflowPolicy<GoalWorkflowInput>(
            $"goal/{goalRun.Id}",
            _ => new ModeWorkflowCompiled<GoalWorkflowInput>(
                definition.Registration,
                definition.Workflow,
                definition.Input,
                $"goal/{goalRun.Id}/execute"),
            serializerOptions,
            EventSink: eventSink,
            LeaseAcquired: async (workflowRun, callbackCt) =>
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
            TerminalStateResolver: ResolveTerminalState,
            DisplayName: "Goal workflow");
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

