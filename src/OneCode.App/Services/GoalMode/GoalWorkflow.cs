using Microsoft.Agents.AI.Workflows;
using OneCode.App.Services.Agent;
using OneCode.Core.Goals;

namespace OneCode.App.Services.GoalMode;

public sealed record GoalWorkflowInput(
    GoalRunId GoalRunId,
    string Goal,
    string ModelId,
    string ToolCapabilityHash);

public sealed record GoalWorkflowState(
    GoalRunId GoalRunId,
    IReadOnlyList<GoalStepSnapshot> Plan,
    IReadOnlyList<GoalStepExecutionEvidence> Executions,
    GoalBudgetSnapshot Budget,
    int CurrentIndex,
    bool HasReplanned,
    GoalRunState State,
    string? FailureSummary = null);

public sealed record GoalWorkflowSignal(bool Continue);

public sealed record GoalWorkflowDecision(bool Continue);

public sealed record GoalWorkflowOutput(
    GoalRunId GoalRunId,
    GoalRunState State,
    int CompletedCount,
    int FailedCount,
    string? FailureSummary);

public interface IGoalWorkflowRuntime
{
    Task BindAsync(GoalRun run, long fencingToken, CancellationToken ct);
    Task<GoalWorkflowState> PlanAsync(GoalWorkflowInput input, CancellationToken ct);
    Task<GoalWorkflowState> ExecuteNextAsync(GoalWorkflowState state, CancellationToken ct);
    Task<GoalWorkflowOutput> CompleteAsync(GoalWorkflowState state, CancellationToken ct);
}

public sealed record GoalWorkflowDefinition(
    Workflow Workflow,
    WorkflowRunRegistration Registration,
    GoalWorkflowInput Input,
    string DefinitionHash);

public sealed class GoalWorkflowCompiler
{
    internal const string SharedScope = "goal-workflow-v1";
    internal const string StateKey = "state";
    private const string PlanExecutorId = "goal-plan-v1";
    private const string StepExecutorId = "goal-step-v1";
    private const string RouterExecutorId = "goal-router-v1";
    private const string CompletionExecutorId = "goal-completion-v1";

    public GoalWorkflowDefinition Compile(
        GoalRun run,
        string modelId,
        string systemPromptHash,
        string toolCapabilityHash,
        IGoalWorkflowRuntime runtime,
        JsonSerializerOptions? serializerOptions = null)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        if (run.IsTerminal)
            throw new InvalidOperationException($"Terminal GoalRun '{run.Id}' cannot be compiled.");
        var definitionHash = ComputeDefinitionHash(
            run,
            modelId,
            systemPromptHash,
            toolCapabilityHash,
            serializerOptions);
        if (!string.Equals(run.DefinitionHash, definitionHash, StringComparison.Ordinal))
            throw new InvalidOperationException("GoalRun definition hash does not match the compiled workflow.");

        var input = new GoalWorkflowInput(run.Id, run.Goal, modelId, toolCapabilityHash);
        var plan = new GoalPlanExecutor(PlanExecutorId, runtime);
        var step = new GoalStepExecutor(StepExecutorId, runtime);
        var router = new GoalRouterExecutor(RouterExecutorId);
        var completion = new GoalCompletionExecutor(CompletionExecutorId, runtime);
        var builder = new WorkflowBuilder(plan)
            .WithName("goal-workflow-v1")
            .WithDescription("Executes a dynamic Goal plan one checkpointed superstep at a time.");
        builder.AddEdge(plan, router, "goal:plan->router", false);
        builder.AddEdge(step, router, "goal:step->router", false);
        builder.AddEdge<GoalWorkflowSignal>(router, step, signal => signal is { Continue: true }, "goal:continue", false);
        builder.AddEdge<GoalWorkflowSignal>(router, completion, signal => signal is { Continue: false }, "goal:complete", false);
        builder.WithOutputFrom(completion);
        var workflow = builder.Build(validateOrphans: true);
        return new GoalWorkflowDefinition(
            workflow,
            new WorkflowRunRegistration($"goal/{run.Id}", "goal", definitionHash),
            input,
            definitionHash);
    }

    public static string ComputeTextHash(string value)
        => WorkflowDefinitionHash.ComputeText(value);

    public static string ComputeToolCapabilityHash(IEnumerable<string> toolNames)
        => ComputeTextHash(JsonSerializer.Serialize(toolNames
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray()));

    public static string ComputeDefinitionHash(
        GoalRun run,
        string modelId,
        string systemPromptHash,
        string toolCapabilityHash,
        JsonSerializerOptions? serializerOptions = null)
    {
        var canonical = JsonSerializer.Serialize(new
        {
            contract = "goal-workflow-v1",
            executors = new[] { PlanExecutorId, StepExecutorId, RouterExecutorId, CompletionExecutorId },
            edges = new[]
            {
                "goal:plan->router", "goal:step->router", "goal:continue", "goal:complete",
            },
            run.Goal,
            run.WorkingDirectory,
            run.WorkspaceFingerprint,
            workspaceId = run.Workspace?.WorkspaceId,
            isolatedPath = run.Workspace?.IsolatedPath,
            worktreeBranch = run.Workspace?.WorktreeBranch,
            targetBranch = run.Workspace?.TargetBranch,
            baseCommit = run.Workspace?.BaseCommit,
            modelId,
            systemPromptHash,
            toolCapabilityHash,
            // Checkpoint 序列化契约纳入恢复凭据（S-06）：序列化配置变化必须改变 Hash，
            // 使 Registry 校验 fail-closed，避免用旧 checkpoint 以新契约反序列化。
            serializerOptions = WorkflowDefinitionHash.DescribeSerializerOptions(serializerOptions),
        });
        return WorkflowDefinitionHash.ComputeText(canonical);
    }
}

internal sealed class GoalPlanExecutor(string id, IGoalWorkflowRuntime runtime)
    : Executor<GoalWorkflowInput, GoalWorkflowDecision>(id)
{
    public override async ValueTask<GoalWorkflowDecision> HandleAsync(
        GoalWorkflowInput message,
        IWorkflowContext context,
        CancellationToken cancellationToken = default)
    {
        var state = await runtime.PlanAsync(message, cancellationToken).ConfigureAwait(false);
        await context.QueueStateUpdateAsync(
            GoalWorkflowCompiler.StateKey,
            state,
            GoalWorkflowCompiler.SharedScope,
            cancellationToken).ConfigureAwait(false);
        return new GoalWorkflowDecision(
            state.State == GoalRunState.Executing && state.CurrentIndex < state.Plan.Count);
    }
}

internal sealed class GoalStepExecutor(string id, IGoalWorkflowRuntime runtime)
    : Executor<GoalWorkflowSignal, GoalWorkflowDecision>(id)
{
    public override async ValueTask<GoalWorkflowDecision> HandleAsync(
        GoalWorkflowSignal message,
        IWorkflowContext context,
        CancellationToken cancellationToken = default)
    {
        var state = await context.ReadStateAsync<GoalWorkflowState>(
            GoalWorkflowCompiler.StateKey,
            GoalWorkflowCompiler.SharedScope,
            cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Goal workflow shared state was not initialized.");
        var updated = await runtime.ExecuteNextAsync(state, cancellationToken).ConfigureAwait(false);
        await context.QueueStateUpdateAsync(
            GoalWorkflowCompiler.StateKey,
            updated,
            GoalWorkflowCompiler.SharedScope,
            cancellationToken).ConfigureAwait(false);
        var shouldContinue = updated.State == GoalRunState.Executing
            && updated.CurrentIndex < updated.Plan.Count;
        return new GoalWorkflowDecision(shouldContinue);
    }
}

internal sealed class GoalRouterExecutor(string id)
    : Executor<GoalWorkflowDecision, GoalWorkflowSignal>(id)
{
    public override ValueTask<GoalWorkflowSignal> HandleAsync(
        GoalWorkflowDecision message,
        IWorkflowContext context,
        CancellationToken cancellationToken = default)
        => ValueTask.FromResult(new GoalWorkflowSignal(message.Continue));
}

internal sealed class GoalCompletionExecutor(string id, IGoalWorkflowRuntime runtime)
    : Executor<GoalWorkflowSignal, GoalWorkflowOutput>(id)
{
    public override async ValueTask<GoalWorkflowOutput> HandleAsync(
        GoalWorkflowSignal message,
        IWorkflowContext context,
        CancellationToken cancellationToken = default)
    {
        var state = await context.ReadStateAsync<GoalWorkflowState>(
            GoalWorkflowCompiler.StateKey,
            GoalWorkflowCompiler.SharedScope,
            cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Goal workflow shared state was not initialized.");
        return await runtime.CompleteAsync(state, cancellationToken).ConfigureAwait(false);
    }
}

internal static class GoalWorkflowStateMapper
{
    public static int FindNextIndex(IReadOnlyList<GoalStepSnapshot> plan)
    {
        for (var index = 0; index < plan.Count; index++)
        {
            if (plan[index].State is GoalStepState.Pending or GoalStepState.InProgress)
                return index;
        }
        return plan.Count;
    }

    public static GoalWorkflowState ToWorkflowState(GoalRun run, int currentIndex, bool hasReplanned)
        => new(
            run.Id,
            run.Plan,
            run.Executions,
            run.Budget,
            currentIndex,
            hasReplanned,
            run.State,
            run.FailureSummary);

    public static GoalStepSnapshot ToSnapshot(GoalItem item)
        => new(
            item.Id,
            item.Description,
            item.SuccessCriteria,
            ToStepState(item.Status),
            item.RequiredTools ?? [],
            item.Depth,
            item.NeedsFurtherDecomposition,
            item.AllowedPaths,
            item.RequiresBuild,
            item.RequiresTests,
            item.Optional,
            DecompositionFallback: false,
            SubTasks: item.SubTasks);

    public static GoalItem ToGoalItem(GoalStepSnapshot item)
        => new()
        {
            Id = item.Id,
            Description = item.Description,
            SuccessCriteria = item.SuccessCriteria,
            Status = ToGoalStatus(item.State),
            RequiredTools = item.RequiredTools,
            Depth = item.Depth,
            NeedsFurtherDecomposition = item.NeedsFurtherDecomposition,
            AllowedPaths = item.AllowedPaths,
            RequiresBuild = item.RequiresBuild,
            RequiresTests = item.RequiresTests,
            Optional = item.Optional,
            SubTasks = item.SubTasks,
        };

    public static GoalStepExecutionEvidence ToEvidence(
        SubGoalExecution execution,
        IReadOnlyList<string> fallbackChangedFiles,
        bool clearChangedFiles = false)
    {
        var source = execution.Evidence;
        // clearChangedFiles: 单代理步骤失败时回执清空文件列表，避免被 replan 取代的失败步触发收尾 change-scope 误判。
        return new GoalStepExecutionEvidence(
            execution.GoalId,
            ToStepState(execution.Status),
            execution.Attempts,
            execution.InputTokens,
            execution.OutputTokens,
            execution.AgentOutput,
            execution.Evaluation,
            clearChangedFiles ? [] : source?.ChangedFiles ?? fallbackChangedFiles,
            source?.ToolExecutions.Select(item => new GoalToolEvidence(item.ToolName, item.IsError, item.Result)).ToArray() ?? [],
            source?.Validations.Select(item => new GoalGateEvidence(item.Gate, item.Passed, item.Skipped, item.Summary)).ToArray() ?? [],
            source?.Diagnostics ?? []);
    }

    public static SubGoalExecution ToSubGoalExecution(GoalStepExecutionEvidence evidence)
        => new(
            evidence.GoalId,
            ToGoalStatus(evidence.State),
            evidence.Attempts,
            evidence.InputTokens,
            evidence.OutputTokens,
            evidence.AgentOutput,
            evidence.Evaluation,
            new SubGoalEvidence(
                evidence.AgentOutput,
                evidence.ChangedFiles,
                evidence.ToolExecutions.Select(item => new GoalToolExecutionEvidence(item.ToolName, item.IsError, item.Result)).ToArray(),
                evidence.Validations.Select(item => new GoalValidationEvidence(item.Gate, item.Passed, item.Skipped, item.Summary)).ToArray(),
                evidence.Diagnostics));

    public static GoalStepState ToStepState(GoalStatus status)
        => status switch
        {
            GoalStatus.Pending => GoalStepState.Pending,
            GoalStatus.InProgress => GoalStepState.InProgress,
            GoalStatus.Completed => GoalStepState.Completed,
            GoalStatus.Failed => GoalStepState.Failed,
            GoalStatus.Skipped => GoalStepState.Skipped,
            _ => throw new ArgumentOutOfRangeException(nameof(status)),
        };

    public static GoalStatus ToGoalStatus(GoalStepState state)
        => state switch
        {
            GoalStepState.Pending => GoalStatus.Pending,
            GoalStepState.InProgress => GoalStatus.InProgress,
            GoalStepState.Completed => GoalStatus.Completed,
            GoalStepState.Failed => GoalStatus.Failed,
            GoalStepState.Skipped => GoalStatus.Skipped,
            _ => throw new ArgumentOutOfRangeException(nameof(state)),
        };
}
