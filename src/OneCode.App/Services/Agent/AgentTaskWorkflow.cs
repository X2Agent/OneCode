using System.Security.Cryptography;
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
    AgentTaskExecutionAccess ExecutionAccess = AgentTaskExecutionAccess.ReadOnly)
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
    long DurationMs)
{
    public bool Success => Status == AgentTaskOutcomeStatus.Succeeded;
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
    private const string DispatcherId = "agent-task-dispatcher-v1";
    private const string ExecutorIdPrefix = "agent-task-v1-";

    /// <summary>Validates the product graph before MAF validates executor bindings and reachability.</summary>
    public static void Validate(IReadOnlyList<AgentWorkflowTask> tasks)
    {
        if (tasks.Count == 0)
            throw new InvalidOperationException("At least one task is required.");

        foreach (var task in tasks)
        {
            if (string.IsNullOrWhiteSpace(task.Id))
                throw new InvalidOperationException("Each task must have a non-empty ID.");
            if (string.IsNullOrWhiteSpace(task.Prompt))
                throw new InvalidOperationException($"Task '{task.Id}' must have a non-empty prompt.");
        }

        var duplicateIds = tasks
            .GroupBy(task => task.Id, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (duplicateIds.Length > 0)
            throw new InvalidOperationException($"Duplicate task IDs detected: {string.Join(", ", duplicateIds)}.");

        var executorIdCollisions = tasks
            .GroupBy(task => NormalizeId(task.Id), StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => string.Join(", ", group.Select(task => task.Id).Order(StringComparer.OrdinalIgnoreCase)))
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (executorIdCollisions.Length > 0)
            throw new InvalidOperationException(
                $"Task IDs produce duplicate normalized executor IDs: {string.Join("; ", executorIdCollisions)}.");

        foreach (var task in tasks)
        {
            if (!Enum.IsDefined(task.ExecutionAccess))
                throw new InvalidOperationException(
                    $"Task '{task.Id}' declares unknown execution access '{task.ExecutionAccess}'.");
        }

        var taskById = tasks.ToDictionary(task => task.Id, StringComparer.OrdinalIgnoreCase);
        foreach (var task in tasks)
        {
            var duplicateDependencies = task.Dependencies
                .GroupBy(dependency => dependency, StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Count() > 1)
                .Select(group => group.Key)
                .ToArray();
            if (duplicateDependencies.Length > 0)
                throw new InvalidOperationException(
                    $"Task '{task.Id}' contains duplicate dependencies: {string.Join(", ", duplicateDependencies)}.");

            foreach (var dependency in task.Dependencies)
            {
                if (!taskById.ContainsKey(dependency))
                    throw new InvalidOperationException($"Task '{task.Id}' depends on unknown task '{dependency}'.");
            }
        }

        var visitState = new Dictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        foreach (var taskId in taskById.Keys.Order(StringComparer.OrdinalIgnoreCase))
            Visit(taskId);

        void Visit(string taskId)
        {
            if (visitState.TryGetValue(taskId, out var state))
            {
                if (state == 1)
                    throw new InvalidOperationException($"Task graph contains a cycle involving task '{taskId}'.");
                return;
            }

            visitState[taskId] = 1;
            foreach (var dependency in taskById[taskId].Dependencies.Order(StringComparer.OrdinalIgnoreCase))
                Visit(dependency);
            visitState[taskId] = 2;
        }
    }

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
        Validate(tasks);

        var orderedTasks = TopologicalOrder(tasks);
        var effectiveDependencies = BuildEffectiveDependencies(orderedTasks);
        var outcomeRegistry = new AgentTaskOutcomeRegistry();
        var dispatcher = new AgentTaskDispatcherExecutor(DispatcherId);
        var taskExecutors = orderedTasks.ToDictionary(
            task => task.Id,
            task => (ExecutorBinding)new AgentTaskExecutor(
                ExecutorIdPrefix + NormalizeId(task.Id),
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
            ComputeDefinitionHash(orderedTasks, effectiveDependencies, terminalTasks, cacheSafeParams, parentCapabilities));
    }

    private static IReadOnlyList<AgentWorkflowTask> TopologicalOrder(IReadOnlyList<AgentWorkflowTask> tasks)
    {
        var taskById = tasks.ToDictionary(task => task.Id, StringComparer.OrdinalIgnoreCase);
        var remainingDependencies = tasks.ToDictionary(
            task => task.Id,
            task => task.Dependencies.Count,
            StringComparer.OrdinalIgnoreCase);
        var dependents = tasks.ToDictionary(
            task => task.Id,
            _ => new List<string>(),
            StringComparer.OrdinalIgnoreCase);
        foreach (var task in tasks)
        {
            foreach (var dependency in task.Dependencies)
                dependents[dependency].Add(task.Id);
        }

        var ready = new SortedSet<string>(
            remainingDependencies.Where(pair => pair.Value == 0).Select(pair => pair.Key),
            StringComparer.OrdinalIgnoreCase);
        var ordered = new List<AgentWorkflowTask>(tasks.Count);
        while (ready.Count > 0)
        {
            var taskId = ready.Min!;
            ready.Remove(taskId);
            ordered.Add(taskById[taskId]);
            foreach (var dependent in dependents[taskId].Order(StringComparer.OrdinalIgnoreCase))
            {
                remainingDependencies[dependent]--;
                if (remainingDependencies[dependent] == 0)
                    ready.Add(dependent);
            }
        }

        if (ordered.Count != tasks.Count)
            throw new InvalidOperationException("Task graph contains a cycle.");
        return ordered;
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<string>> BuildEffectiveDependencies(
        IReadOnlyList<AgentWorkflowTask> orderedTasks)
    {
        var dependencies = orderedTasks.ToDictionary(
            task => task.Id,
            task => task.Dependencies.Order(StringComparer.OrdinalIgnoreCase).ToList(),
            StringComparer.OrdinalIgnoreCase);
        var writeTasks = orderedTasks.Where(task => task.ExecutionAccess == AgentTaskExecutionAccess.Write).ToArray();
        for (var index = 1; index < writeTasks.Length; index++)
        {
            var previousWrite = writeTasks[index - 1].Id;
            var currentWrite = writeTasks[index].Id;
            if (!IsReachable(previousWrite, currentWrite, dependencies))
                dependencies[currentWrite].Add(previousWrite);
        }

        return dependencies.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<string>)pair.Value.Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase).ToArray(),
            StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsReachable(
        string ancestor,
        string descendant,
        IReadOnlyDictionary<string, List<string>> dependencies)
    {
        var pending = new Stack<string>();
        pending.Push(descendant);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            if (!visited.Add(current))
                continue;
            foreach (var dependency in dependencies[current])
            {
                if (string.Equals(dependency, ancestor, StringComparison.OrdinalIgnoreCase))
                    return true;
                pending.Push(dependency);
            }
        }
        return false;
    }

    private static string ComputeDefinitionHash(
        IReadOnlyList<AgentWorkflowTask> tasks,
        IReadOnlyDictionary<string, IReadOnlyList<string>> dependencies,
        IReadOnlyList<AgentWorkflowTask> terminalTasks,
        CacheSafeParams? cacheSafeParams,
        ToolCapabilitySet? parentCapabilities)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("schema", "agent-task-workflow-definition-v2");
            writer.WriteStartArray("tasks");
            foreach (var task in tasks)
            {
                writer.WriteStartObject();
                writer.WriteString("id", task.Id);
                writer.WriteString("executorId", ExecutorIdPrefix + NormalizeId(task.Id));
                writer.WriteString("agent", task.Agent);
                writer.WriteString("description", task.Description);
                writer.WriteString("prompt", task.Prompt);
                writer.WriteNumber("executionAccess", (int)task.ExecutionAccess);
                writer.WriteBoolean("injectUpstreamResults", task.InjectUpstreamResults);
                writer.WriteStartArray("dependencies");
                foreach (var dependency in dependencies[task.Id])
                    writer.WriteStringValue(dependency);
                writer.WriteEndArray();
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteStartArray("outputs");
            foreach (var terminal in terminalTasks)
                writer.WriteStringValue(terminal.Id);
            writer.WriteEndArray();
            writer.WriteString("systemPrompt", cacheSafeParams?.SystemPrompt);
            writer.WriteString("model", cacheSafeParams?.ModelId);
            if (cacheSafeParams?.ThinkingBudget is { } thinkingBudget)
                writer.WriteNumber("thinkingBudget", thinkingBudget);
            else
                writer.WriteNull("thinkingBudget");
            WriteToolNames(writer, "cacheTools", cacheSafeParams?.Tools?.Select(tool => tool.Name));
            WriteCapabilities(writer, "cacheCapabilities", cacheSafeParams?.ToolCapabilities);
            WriteCapabilities(writer, "parentCapabilities", parentCapabilities);
            writer.WriteString("dispatcherId", DispatcherId);
            writer.WriteString("outcomeContract", "AgentTaskOutcome:v1");
            writer.WriteString("mafVersion", "1.15.0");
            writer.WriteEndObject();
        }

        return Convert.ToHexString(SHA256.HashData(stream.ToArray())).ToLowerInvariant();
    }

    private static void WriteToolNames(Utf8JsonWriter writer, string propertyName, IEnumerable<string?>? toolNames)
    {
        writer.WriteStartArray(propertyName);
        foreach (var toolName in (toolNames ?? []).Where(name => name is not null).Order(StringComparer.OrdinalIgnoreCase))
            writer.WriteStringValue(toolName);
        writer.WriteEndArray();
    }

    private static void WriteCapabilities(
        Utf8JsonWriter writer,
        string propertyName,
        ToolCapabilitySet? capabilities)
    {
        writer.WritePropertyName(propertyName);
        if (capabilities is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartObject();
        WriteToolNames(writer, "allowedToolNames", capabilities.AllowedToolNames);
        writer.WriteNumber("allowedCategories", (int)capabilities.AllowedCategories);
        writer.WriteNumber("maximumRisk", (int)capabilities.MaximumRisk);
        writer.WriteBoolean("allowDynamicActivation", capabilities.AllowDynamicActivation);
        writer.WriteBoolean("allowSubAgents", capabilities.AllowSubAgents);
        writer.WriteEndObject();
    }

    internal static string NormalizeId(string id)
    {
        var builder = new StringBuilder(id.Length);
        foreach (var character in id)
            builder.Append(char.IsLetterOrDigit(character) || character is '-' or '_' ? char.ToLowerInvariant(character) : '-');
        return builder.ToString();
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
                    ParentCapabilities: parentCapabilities),
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
                    stopwatch.ElapsedMilliseconds));
            }

            return outcomeRegistry.Set(new AgentTaskOutcome(
                task.Id,
                task.Description,
                AgentTaskOutcomeStatus.Succeeded,
                result.Output,
                null,
                result.TurnsCompleted,
                result.MaxTurnsReached,
                stopwatch.ElapsedMilliseconds));
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
