using System.Text;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using OneCode.Infrastructure.Agent;

namespace OneCode.App.Services.Agent;

/// <summary>Defines one task in a MAF-backed agent workflow.</summary>
public sealed record AgentWorkflowTask(
    string Id,
    string Prompt,
    string Agent = "general-purpose",
    string? Description = null,
    IReadOnlyList<string>? DependsOn = null,
    bool InjectUpstreamResults = true,
    AgentTaskExecutionAccess ExecutionAccess = AgentTaskExecutionAccess.ReadOnly,
    // 自定义代理（.onecode/agents/*.md）派工时由工具层填充。
    string? Instructions = null,
    IReadOnlyList<string>? AllowedTools = null,
    // 工作目录覆盖（GOAL 隔离 worktree 场景）。null 时 fork 使用会话工作目录访问器。
    string? WorkingDirectory = null)
{
    /// <summary>Returns the normalized dependency list supplied by the approved graph.</summary>
    public IReadOnlyList<string> Dependencies { get; } = DependsOn ?? [];
}

/// <summary>Declares whether a task may mutate its allowed workspace.</summary>
public enum AgentTaskExecutionAccess
{
    ReadOnly,
    Write,
}

/// <summary>Contains one deterministic compiled task graph definition.</summary>
public sealed record AgentTaskWorkflowDefinition(
    Workflow Workflow,
    IReadOnlyList<AgentWorkflowTask> Tasks,
    IReadOnlyDictionary<string, IReadOnlyList<string>> EffectiveDependencies,
    IReadOnlyList<string> TerminalTaskIds,
    string DefinitionHash);

/// <summary>Represents the terminal state of an agent workflow task.</summary>
public enum AgentTaskOutcomeStatus
{
    Succeeded,
    Failed,
    Blocked,
    Cancelled,
}

/// <summary>Contains the structured outcome emitted by every task executor.</summary>
public sealed record AgentTaskOutcome(
    string TaskId,
    string? Description,
    AgentTaskOutcomeStatus Status,
    string? Output,
    string? Error,
    int TurnsCompleted,
    bool MaxTurnsReached,
    long DurationMs,
    // fork 事务提交的变更文件与 token 消耗（GOAL 步内 DAG 的证据与预算依赖此聚合）。
    IReadOnlyList<string>? ChangedFiles = null,
    long InputTokens = 0,
    long OutputTokens = 0)
{
    public bool Success => Status == AgentTaskOutcomeStatus.Succeeded;

    public IReadOnlyList<string> ChangedFiles { get; } = ChangedFiles ?? [];
}

/// <summary>Contains the aggregate result returned by the workflow host.</summary>
public sealed record AgentWorkflowResult(
    IReadOnlyList<AgentTaskOutcome> TaskOutcomes,
    string FinalOutput,
    long TotalDurationMs,
    int TotalTurnsCompleted)
{
    public bool AllSucceeded => TaskOutcomes.All(outcome => outcome.Success);
}

/// <summary>Validates and compiles approved agent tasks into a deterministic MAF workflow.</summary>
public sealed class AgentTaskWorkflowCompiler(IAgentRunner runner, ILogger<AgentTaskWorkflowCompiler> logger)
{
    /// <summary>Validates the product graph before MAF validates executor bindings and reachability.</summary>
    public static void Validate(IReadOnlyList<AgentWorkflowTask> tasks)
        => AgentWorkflowTopology.Validate(tasks);

    /// <summary>Builds a new workflow and a new executor set for one run.</summary>
    public Workflow Compile(
        IReadOnlyList<AgentWorkflowTask> tasks,
        CacheSafeParams? cacheSafeParams,
        ToolCapabilitySet? parentCapabilities)
        => CompileDefinition(tasks, cacheSafeParams, parentCapabilities).Workflow;

    /// <summary>Builds a deterministic workflow together with its normalized topology and definition hash.</summary>
    public AgentTaskWorkflowDefinition CompileDefinition(
        IReadOnlyList<AgentWorkflowTask> tasks,
        CacheSafeParams? cacheSafeParams,
        ToolCapabilitySet? parentCapabilities)
    {
        AgentWorkflowTopology.Validate(tasks);

        var orderedTasks = WorkflowTopology.KahnOrder(
            tasks, task => task.Id, task => task.Dependencies, "Task graph contains a cycle.");
        var effectiveDependencies = AgentWorkflowTopology.BuildEffectiveDependencies(orderedTasks);
        var outcomeRegistry = new AgentTaskOutcomeRegistry();
        var dispatcher = new AgentTaskDispatcherExecutor(AgentWorkflowTopology.DispatcherId);
        var taskExecutors = orderedTasks.ToDictionary(
            task => task.Id,
            task => (ExecutorBinding)new AgentTaskExecutor(
                AgentWorkflowTopology.ExecutorIdPrefix + AgentWorkflowTopology.NormalizeId(task.Id),
                task,
                effectiveDependencies[task.Id],
                runner,
                outcomeRegistry,
                cacheSafeParams,
                parentCapabilities,
                logger),
            StringComparer.OrdinalIgnoreCase);

        var builder = new WorkflowBuilder(dispatcher)
            .WithName("parallel-agent-task-workflow")
            .WithDescription("Executes a validated agent task graph with MAF fan-out, fan-in, and supersteps.");

        var roots = orderedTasks
            .Where(task => effectiveDependencies[task.Id].Count == 0)
            .Select(task => taskExecutors[task.Id])
            .ToArray();
        builder.AddFanOutEdge(dispatcher, roots, "dispatch-roots");

        foreach (var task in orderedTasks.Where(task => effectiveDependencies[task.Id].Count > 0))
        {
            var sources = effectiveDependencies[task.Id]
                .Order(StringComparer.OrdinalIgnoreCase)
                .Select(dependency => taskExecutors[dependency])
                .ToArray();
            if (sources.Length == 1)
                builder.AddEdge(sources[0], taskExecutors[task.Id], $"dependency:{sources[0].Id}->{taskExecutors[task.Id].Id}", false);
            else
                builder.AddFanInBarrierEdge(sources, taskExecutors[task.Id], $"barrier:{taskExecutors[task.Id].Id}");
        }

        var terminalTasks = orderedTasks
            .Where(candidate => orderedTasks.All(task => !effectiveDependencies[task.Id].Contains(candidate.Id, StringComparer.OrdinalIgnoreCase)))
            .ToArray();
        builder.WithOutputFrom(terminalTasks.Select(task => taskExecutors[task.Id]).ToArray());
        var workflow = builder.Build(validateOrphans: true);
        return new AgentTaskWorkflowDefinition(
            workflow,
            orderedTasks,
            effectiveDependencies,
            terminalTasks.Select(task => task.Id).ToArray(),
            AgentWorkflowTopology.ComputeDefinitionHash(orderedTasks, effectiveDependencies, terminalTasks, cacheSafeParams, parentCapabilities));
    }
}

/// <summary>Runs compiled agent task workflows through the MAF execution environment.</summary>
public sealed class AgentTaskWorkflowHost(ILogger<AgentTaskWorkflowHost> logger)
{
    private static readonly TimeSpan WorkflowTimeout = TimeSpan.FromMinutes(30);

    /// <summary>Runs a compiled workflow and returns its structured terminal outcomes.</summary>
    public async Task<AgentWorkflowResult> RunAsync(
        Workflow workflow,
        IReadOnlyList<AgentWorkflowTask> tasks,
        CancellationToken ct = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var outcomes = new Dictionary<string, AgentTaskOutcome>(StringComparer.OrdinalIgnoreCase);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(WorkflowTimeout);

        try
        {
            await using var run = await InProcessExecution.Default
                .RunStreamingAsync(workflow, new AgentTaskStartMessage(), $"agent-dag-{Guid.NewGuid():N}", timeout.Token)
                .ConfigureAwait(false);

            await foreach (var workflowEvent in run.WatchStreamAsync(timeout.Token).ConfigureAwait(false))
            {
                switch (workflowEvent)
                {
                    case ExecutorCompletedEvent { Data: AgentTaskOutcome outcome }:
                        outcomes[outcome.TaskId] = outcome;
                        break;
                    case WorkflowOutputEvent outputEvent when outputEvent.Data is AgentTaskOutcome outcome:
                        outcomes[outcome.TaskId] = outcome;
                        break;
                    case WorkflowErrorEvent errorEvent:
                        logger.LogError(errorEvent.Exception, "Agent task workflow failed");
                        throw new InvalidOperationException("Agent task workflow failed.", errorEvent.Exception);
                }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && timeout.IsCancellationRequested)
        {
            logger.LogWarning("Agent task workflow timed out after {Timeout}", WorkflowTimeout);
        }

        stopwatch.Stop();
        var orderedOutcomes = tasks.Select(task => outcomes.GetValueOrDefault(task.Id)
            ?? new AgentTaskOutcome(
                task.Id,
                task.Description,
                timeout.IsCancellationRequested && !ct.IsCancellationRequested
                    ? AgentTaskOutcomeStatus.Cancelled
                    : AgentTaskOutcomeStatus.Failed,
                null,
                timeout.IsCancellationRequested && !ct.IsCancellationRequested
                    ? $"Workflow timed out after {WorkflowTimeout.TotalMinutes:F0} minutes."
                    : "Task did not produce a terminal outcome.",
                0,
                false,
                0)).ToArray();

        return new AgentWorkflowResult(
            orderedOutcomes,
            BuildFinalOutput(orderedOutcomes),
            stopwatch.ElapsedMilliseconds,
            orderedOutcomes.Sum(outcome => outcome.TurnsCompleted));
    }

    private static string BuildFinalOutput(IReadOnlyList<AgentTaskOutcome> outcomes)
    {
        var builder = new StringBuilder();
        foreach (var outcome in outcomes)
        {
            builder.AppendLine(CultureInfo.InvariantCulture, $"### {outcome.Description ?? outcome.TaskId}");
            builder.AppendLine(outcome.Success ? outcome.Output ?? "(no output)" : $"**Error**: {outcome.Error}");
            builder.AppendLine();
        }
        return builder.ToString().TrimEnd();
    }
}

internal sealed record AgentTaskStartMessage;

internal sealed class AgentTaskDispatcherExecutor(string id) : Executor<AgentTaskStartMessage, AgentTaskStartMessage>(id)
{
    public override ValueTask<AgentTaskStartMessage> HandleAsync(
        AgentTaskStartMessage message,
        IWorkflowContext context,
        CancellationToken cancellationToken = default)
        => ValueTask.FromResult(message);
}

internal sealed class AgentTaskExecutor(
    string id,
    AgentWorkflowTask task,
    IReadOnlyList<string> effectiveDependencies,
    IAgentRunner runner,
    AgentTaskOutcomeRegistry outcomeRegistry,
    CacheSafeParams? cacheSafeParams,
    ToolCapabilitySet? parentCapabilities,
    ILogger logger) : Executor<object, AgentTaskOutcome>(id)
{
    public override async ValueTask<AgentTaskOutcome> HandleAsync(
        object message,
        IWorkflowContext context,
        CancellationToken cancellationToken = default)
    {
        var dependencyOutcomes = effectiveDependencies
            .Order(StringComparer.OrdinalIgnoreCase)
            .Select(dependency => outcomeRegistry.Get(dependency))
            .ToArray();
        var blockedDependencies = dependencyOutcomes.Where(outcome => !outcome.Success).ToArray();
        if (blockedDependencies.Length > 0)
        {
            return outcomeRegistry.Set(new AgentTaskOutcome(
                task.Id,
                task.Description,
                AgentTaskOutcomeStatus.Blocked,
                null,
                $"Blocked by upstream task(s): {string.Join(", ", blockedDependencies.Select(outcome => outcome.TaskId))}.",
                0,
                false,
                0));
        }

        var stopwatch = Stopwatch.StartNew();
        var explicitDependencyIds = task.Dependencies.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var prompt = BuildPrompt(
            task,
            dependencyOutcomes.Where(outcome => explicitDependencyIds.Contains(outcome.TaskId)).ToArray());
        try
        {
            var result = await runner.RunAsync(
                new AgentRunRequest(
                    prompt,
                    task.Agent,
                    task.Description,
                    CacheSafeParams: CreateTaskCacheSafeParams(task, cacheSafeParams),
                    ParentCapabilities: parentCapabilities,
                    Instructions: task.Instructions,
                    AllowedToolNames: task.AllowedTools,
                    WorkingDirectory: task.WorkingDirectory),
                cancellationToken).ConfigureAwait(false);
            stopwatch.Stop();

            if (result.Error is { } error)
            {
                return outcomeRegistry.Set(new AgentTaskOutcome(
                    task.Id,
                    task.Description,
                    AgentTaskOutcomeStatus.Failed,
                    null,
                    $"[{error.Type}] {error.Detail}",
                    result.TurnsCompleted,
                    result.MaxTurnsReached,
                    stopwatch.ElapsedMilliseconds,
                    result.ChangedFiles ?? [],
                    result.InputTokens,
                    result.OutputTokens));
            }

            return outcomeRegistry.Set(new AgentTaskOutcome(
                task.Id,
                task.Description,
                AgentTaskOutcomeStatus.Succeeded,
                result.Output,
                null,
                result.TurnsCompleted,
                result.MaxTurnsReached,
                stopwatch.ElapsedMilliseconds,
                result.ChangedFiles ?? [],
                result.InputTokens,
                result.OutputTokens));
        }
        catch (OperationCanceledException)
        {
            // External cancellation is a workflow interruption; do not turn it
            // into a terminal task outcome that prevents a later resume.
            throw;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            logger.LogError(ex, "Agent workflow task '{TaskId}' failed", task.Id);
            return outcomeRegistry.Set(new AgentTaskOutcome(
                task.Id,
                task.Description,
                AgentTaskOutcomeStatus.Failed,
                null,
                ex.Message,
                0,
                false,
                stopwatch.ElapsedMilliseconds));
        }
    }

    private static CacheSafeParams? CreateTaskCacheSafeParams(
        AgentWorkflowTask task,
        CacheSafeParams? parent)
    {
        if (task.ExecutionAccess == AgentTaskExecutionAccess.Write)
            return parent;

        var readOnlyNames = PipelineProfileBehavior.ReadOnlyAgentTools
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var tools = parent?.Tools?
            .Where(tool => tool is AIFunction function && readOnlyNames.Contains(function.Name))
            .ToList()
            ?? [];
        var readOnlyCapabilities = ToolCapabilitySet.CreateUnrestricted(readOnlyNames);
        return new CacheSafeParams
        {
            SystemPrompt = parent?.SystemPrompt,
            ModelId = parent?.ModelId,
            ThinkingBudget = parent?.ThinkingBudget,
            Tools = tools,
            ToolCapabilities = parent?.ToolCapabilities?.Intersect(readOnlyCapabilities)
                ?? readOnlyCapabilities,
            Metadata = parent?.Metadata,
        };
    }

    private static string BuildPrompt(AgentWorkflowTask task, IReadOnlyList<AgentTaskOutcome> dependencies)
    {
        if (!task.InjectUpstreamResults || dependencies.Count == 0)
            return task.Prompt;

        var builder = new StringBuilder("## Context from upstream tasks:\n");
        foreach (var dependency in dependencies)
        {
            builder.AppendLine().AppendLine(
                CultureInfo.InvariantCulture,
                $"### {dependency.Description ?? dependency.TaskId} ({dependency.TaskId}):");
            builder.AppendLine(dependency.Output ?? "(no output)");
        }
        builder.AppendLine().AppendLine("## Your task:").Append(task.Prompt);
        return builder.ToString();
    }
}

internal sealed class AgentTaskOutcomeRegistry
{
    private readonly ConcurrentDictionary<string, AgentTaskOutcome> _outcomes = new(StringComparer.OrdinalIgnoreCase);

    public AgentTaskOutcome Get(string taskId)
        => _outcomes.TryGetValue(taskId, out var outcome)
            ? outcome
            : throw new InvalidOperationException($"Dependency '{taskId}' did not produce a terminal outcome.");

    public AgentTaskOutcome Set(AgentTaskOutcome outcome)
    {
        _outcomes[outcome.TaskId] = outcome;
        return outcome;
    }
}
