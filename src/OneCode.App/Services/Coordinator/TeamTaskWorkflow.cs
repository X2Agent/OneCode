using System.Security.Cryptography;
using Microsoft.Agents.AI.Workflows;
using OneCode.App.Services.Agent;
using OneCode.Core.Coordinator;
using OneCode.Core.Workflows;

namespace OneCode.App.Services.Coordinator;

/// <summary>Durable, serializable input for one Team task DAG run.</summary>
internal sealed record TeamTaskWorkflowInput(TeamRunId RunId, string DefinitionHash);

/// <summary>Terminal state of one Team task executor.</summary>
internal enum TeamTaskOutcomeStatus
{
    Succeeded,
    Failed,
    Blocked,
    Cancelled,
}

/// <summary>Structured outcome emitted by every Team task executor.</summary>
internal sealed record TeamTaskOutcome(
    string TaskId,
    string Title,
    TeamTaskOutcomeStatus Status,
    string? Summary,
    string? Error,
    int TurnsCompleted,
    bool MaxTurnsReached,
    long DurationMs)
{
    public bool Success => Status == TeamTaskOutcomeStatus.Succeeded;
}

/// <summary>Aggregate typed result returned by the Team workflow host.</summary>
internal sealed record TeamTaskWorkflowResult(
    DurableWorkflowRunResult Durable,
    IReadOnlyList<TeamTaskOutcome> Outcomes)
{
    public bool AllSucceeded => Outcomes.Count > 0 && Outcomes.All(outcome => outcome.Success);
}

/// <summary>Contains one deterministic compiled Team task graph definition.</summary>
internal sealed record TeamTaskWorkflowDefinition(
    Workflow Workflow,
    WorkflowRunRegistration Registration,
    TeamTaskWorkflowInput Input,
    IReadOnlyDictionary<string, IReadOnlyList<string>> EffectiveDependencies,
    TeamTaskOutcomeRegistry OutcomeRegistry);

/// <summary>
/// Binds one Team run execution to concrete runtime services.
/// Implementations are created per run and must not be shared across runs.
/// </summary>
internal interface ITeamTaskWorkflowRuntime
{
    /// <summary>Binds the claimed business aggregate and current fencing token before any executor runs.</summary>
    Task BindAsync(TeamRun run, long fencingToken, CancellationToken ct);

    /// <summary>Executes exactly one approved task as an idempotent unit (GroupChat/Magentic inside).</summary>
    Task<TeamRunResult> ExecuteTaskAsync(TeamTaskDefinition task, CancellationToken ct);
}

/// <summary>
/// Compiles an approved Team task graph into a deterministic MAF workflow.
/// Approved dependencies drive fan-out/fan-in; write tasks are serialized in stable
/// topological order; every executor emits a structured outcome and failed upstreams
/// block downstream tasks. Dynamic content never changes Executor/Edge IDs.
/// </summary>
internal sealed class TeamTaskWorkflowCompiler
{
    private const string DispatcherId = "team-task-dispatcher-v1";
    private const string ExecutorIdPrefix = "team-task-v1-";

    public TeamTaskWorkflowDefinition Compile(
        TeamRun run,
        TeamConfig config,
        string modelId,
        ITeamTaskWorkflowRuntime runtime,
        JsonSerializerOptions? serializerOptions = null)
    {
        var plan = run.Plan ?? throw new InvalidOperationException($"TeamRun '{run.Id}' has no approved plan.");
        Validate(plan.Tasks);

        var orderedTasks = TopologicalOrder(plan.Tasks);
        var effectiveDependencies = BuildEffectiveDependencies(orderedTasks);
        var outcomeRegistry = new TeamTaskOutcomeRegistry();
        var dispatcher = new TeamTaskDispatcherExecutor(DispatcherId);
        var taskExecutors = orderedTasks.ToDictionary(
            task => task.Id,
            task => (ExecutorBinding)new TeamTaskExecutor(
                ExecutorIdPrefix + NormalizeId(task.Id),
                task,
                effectiveDependencies[task.Id],
                runtime,
                outcomeRegistry),
            StringComparer.OrdinalIgnoreCase);
        SeedPersistedOutcomes(run, outcomeRegistry);

        var builder = new WorkflowBuilder(dispatcher)
            .WithName("team-task-dag-workflow-v1")
            .WithDescription("Executes an approved Team task graph with MAF fan-out, fan-in, and supersteps.");

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
            .Where(candidate => orderedTasks.All(task =>
                !effectiveDependencies[task.Id].Contains(candidate.Id, StringComparer.OrdinalIgnoreCase)))
            .ToArray();
        builder.WithOutputFrom(terminalTasks.Select(task => taskExecutors[task.Id]).ToArray());

        var workflow = builder.Build(validateOrphans: true);
        var definitionHash = ComputeDefinitionHash(
            run,
            config,
            modelId,
            orderedTasks,
            effectiveDependencies,
            terminalTasks,
            serializerOptions);
        var registration = new WorkflowRunRegistration($"team/{run.Id}", "team", definitionHash);
        return new TeamTaskWorkflowDefinition(
            workflow,
            registration,
            new TeamTaskWorkflowInput(run.Id, definitionHash),
            effectiveDependencies,
            outcomeRegistry);
    }

    private static void SeedPersistedOutcomes(TeamRun run, TeamTaskOutcomeRegistry registry)
    {
        foreach (var task in run.TaskGraph?.Tasks ?? [])
        {
            var status = task.Status switch
            {
                TeamTaskStatus.Succeeded => TeamTaskOutcomeStatus.Succeeded,
                TeamTaskStatus.Failed => TeamTaskOutcomeStatus.Failed,
                TeamTaskStatus.Skipped => TeamTaskOutcomeStatus.Blocked,
                TeamTaskStatus.Cancelled => TeamTaskOutcomeStatus.Cancelled,
                _ => (TeamTaskOutcomeStatus?)null,
            };
            if (status is not { } terminalStatus)
                continue;

            registry.Set(new TeamTaskOutcome(
                task.Definition.Id,
                task.Definition.Title,
                terminalStatus,
                task.Summary,
                task.Failure?.Detail,
                0,
                false,
                0));
        }
    }

    internal static void Validate(IReadOnlyList<TeamTaskDefinition> tasks)
    {
        if (tasks.Count == 0)
            throw new InvalidOperationException("Team task graph requires at least one task.");

        var duplicateIds = tasks
            .GroupBy(task => task.Id, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (duplicateIds.Length > 0)
            throw new InvalidOperationException($"Duplicate Team task IDs detected: {string.Join(", ", duplicateIds)}.");

        var executorIdCollisions = tasks
            .GroupBy(task => NormalizeId(task.Id), StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => string.Join(", ", group.Select(task => task.Id).Order(StringComparer.OrdinalIgnoreCase)))
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (executorIdCollisions.Length > 0)
        {
            throw new InvalidOperationException(
                $"Team task IDs produce duplicate normalized executor IDs: {string.Join("; ", executorIdCollisions)}.");
        }

        var taskById = tasks.ToDictionary(task => task.Id, StringComparer.OrdinalIgnoreCase);
        foreach (var task in tasks)
        {
            if (string.IsNullOrWhiteSpace(task.Id))
                throw new InvalidOperationException("Each Team task must have a non-empty ID.");
            if (!Enum.IsDefined(task.ToolPolicy))
                throw new InvalidOperationException($"Team task '{task.Id}' declares unknown tool policy '{task.ToolPolicy}'.");
            foreach (var dependency in task.DependsOn)
            {
                if (!taskById.ContainsKey(dependency))
                    throw new InvalidOperationException($"Team task '{task.Id}' depends on unknown task '{dependency}'.");
            }
        }

        _ = TopologicalOrder(tasks);
    }

    private static IReadOnlyList<TeamTaskDefinition> TopologicalOrder(IReadOnlyList<TeamTaskDefinition> tasks)
    {
        var taskById = tasks.ToDictionary(task => task.Id, StringComparer.OrdinalIgnoreCase);
        var remainingDependencies = tasks.ToDictionary(
            task => task.Id, task => task.DependsOn.Count, StringComparer.OrdinalIgnoreCase);
        var dependents = tasks.ToDictionary(
            task => task.Id, _ => new List<string>(), StringComparer.OrdinalIgnoreCase);
        foreach (var task in tasks)
        {
            foreach (var dependency in task.DependsOn)
                dependents[dependency].Add(task.Id);
        }

        var ready = new SortedSet<string>(
            remainingDependencies.Where(pair => pair.Value == 0).Select(pair => pair.Key),
            StringComparer.OrdinalIgnoreCase);
        var ordered = new List<TeamTaskDefinition>(tasks.Count);
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
            throw new InvalidOperationException("Team task graph contains a dependency cycle.");
        return ordered;
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<string>> BuildEffectiveDependencies(
        IReadOnlyList<TeamTaskDefinition> orderedTasks)
    {
        var dependencies = orderedTasks.ToDictionary(
            task => task.Id,
            task => task.DependsOn.Order(StringComparer.OrdinalIgnoreCase).ToList(),
            StringComparer.OrdinalIgnoreCase);
        var writeTasks = orderedTasks.Where(task => task.ToolPolicy == TeamToolPolicy.WriteAllowed).ToArray();
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
        TeamRun run,
        TeamConfig config,
        string modelId,
        IReadOnlyList<TeamTaskDefinition> tasks,
        IReadOnlyDictionary<string, IReadOnlyList<string>> dependencies,
        IReadOnlyList<TeamTaskDefinition> terminalTasks,
        JsonSerializerOptions? serializerOptions)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("schema", "team-task-workflow-definition-v1");
            // 定义哈希标识“工作流定义”，不依赖具体 Run 实例（runId 已由 Registration.RunId 承载），
            // 保证等价定义（含输入任务顺序置换）产生一致哈希。
            writer.WriteString("team", run.TeamName);
            writer.WriteNumber("mode", (int)config.Mode);
            writer.WriteString("modelId", modelId);
            writer.WriteNumber("maxTurns", config.MaxTurns);
            writer.WriteStartArray("members");
            foreach (var member in config.Members.OrderBy(member => member.AgentId, StringComparer.Ordinal))
            {
                writer.WriteStartObject();
                writer.WriteString("agentId", member.AgentId);
                writer.WriteString("role", member.Role);
                writer.WriteString("systemPrompt", member.SystemPrompt);
                writer.WriteStartArray("allowedTools");
                foreach (var tool in (member.AllowedTools ?? []).Order(StringComparer.OrdinalIgnoreCase))
                    writer.WriteStringValue(tool);
                writer.WriteEndArray();
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteStartArray("tasks");
            foreach (var task in tasks)
            {
                writer.WriteStartObject();
                writer.WriteString("id", task.Id);
                writer.WriteString("executorId", ExecutorIdPrefix + NormalizeId(task.Id));
                writer.WriteNumber("kind", (int)task.Kind);
                writer.WriteString("assigneeRole", task.AssigneeRole);
                writer.WriteNumber("toolPolicy", (int)task.ToolPolicy);
                writer.WriteBoolean("required", task.Required);
                writer.WriteStartArray("requiredTools");
                foreach (var tool in (task.RequiredTools ?? []).Order(StringComparer.OrdinalIgnoreCase))
                    writer.WriteStringValue(tool);
                writer.WriteEndArray();
                writer.WriteStartArray("allowedPaths");
                foreach (var path in (task.AllowedPaths ?? []).Order(StringComparer.OrdinalIgnoreCase))
                    writer.WriteStringValue(path);
                writer.WriteEndArray();
                writer.WriteStartArray("requiredGates");
                foreach (var gate in (task.RequiredGates ?? []).Order(StringComparer.OrdinalIgnoreCase))
                    writer.WriteStringValue(gate);
                writer.WriteEndArray();
                writer.WriteString("title", task.Title);
                writer.WriteStartArray("acceptanceCriteria");
                foreach (var criterion in task.AcceptanceCriteria.Order(StringComparer.Ordinal))
                    writer.WriteStringValue(criterion);
                writer.WriteEndArray();
                writer.WriteStartArray("dependencies");
                foreach (var dependency in dependencies[task.Id])
                    writer.WriteStringValue(dependency);
                writer.WriteEndArray();
                writer.WriteStartArray("expectedOutputs");
                foreach (var output in (task.ExpectedOutputs ?? []).Order(StringComparer.Ordinal))
                    writer.WriteStringValue(output);
                writer.WriteEndArray();
                writer.WriteNumber("maxAttempts", task.MaxAttempts);
                if (task.RetryPolicy is { } policy)
                {
                    writer.WriteStartObject("retryPolicy");
                    writer.WriteNumber("maxAttempts", policy.MaxAttempts);
                    writer.WriteString("initialDelay", policy.InitialDelay.ToString());
                    writer.WriteString("maxDelay", policy.MaxDelay.ToString());
                    writer.WriteNumber("backoffMultiplier", policy.BackoffMultiplier);
                    writer.WriteStartArray("retryableErrorFingerprints");
                    foreach (var fingerprint in (policy.RetryableErrorFingerprints ?? []).Order(StringComparer.Ordinal))
                        writer.WriteStringValue(fingerprint);
                    writer.WriteEndArray();
                    writer.WriteEndObject();
                }
                else
                {
                    writer.WriteNull("retryPolicy");
                }
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteStartArray("requiredGates");
            foreach (var gate in (run.Plan?.RequiredGates ?? [])
                .OrderBy(gate => gate.Id, StringComparer.Ordinal))
            {
                writer.WriteStartObject();
                writer.WriteString("id", gate.Id);
                writer.WriteNumber("kind", (int)gate.Kind);
                writer.WriteBoolean("required", gate.Required);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteStartArray("outputs");
            foreach (var terminal in terminalTasks)
                writer.WriteStringValue(ExecutorIdPrefix + NormalizeId(terminal.Id));
            writer.WriteEndArray();
            writer.WriteString("contract", "TeamTaskOutcome:v1");
            writer.WriteString("maf", "1.15.0");
            writer.WriteString(
                "serializerOptions",
                serializerOptions is null
                    ? "default"
                    : JsonSerializer.Serialize(serializerOptions));
            writer.WriteEndObject();
        }

        return Convert.ToHexString(SHA256.HashData(stream.ToArray())).ToLowerInvariant();
    }

    internal static string NormalizeId(string id)
    {
        var chars = id.Trim().ToLowerInvariant()
            .Select(character => char.IsLetterOrDigit(character) ? character : '-')
            .ToArray();
        var normalized = new string(chars);
        while (normalized.Contains("--", StringComparison.Ordinal))
            normalized = normalized.Replace("--", "-", StringComparison.Ordinal);
        return normalized.Trim('-');
    }
}

internal sealed class TeamTaskOutcomeRegistry
{
    private readonly ConcurrentDictionary<string, TeamTaskOutcome> _outcomes = new(StringComparer.OrdinalIgnoreCase);

    public TeamTaskOutcome Get(string taskId)
        => _outcomes.TryGetValue(taskId, out var outcome)
            ? outcome
            : throw new InvalidOperationException($"Team task '{taskId}' has no completed upstream outcome.");

    public bool TryGet(string taskId, out TeamTaskOutcome outcome)
        => _outcomes.TryGetValue(taskId, out outcome!);

    public TeamTaskOutcome Set(TeamTaskOutcome outcome)
    {
        _outcomes[outcome.TaskId] = outcome;
        return outcome;
    }

    /// <summary>All recorded outcomes (executed and blocked). The registry is per-compile and shared by all executors.</summary>
    public IReadOnlyList<TeamTaskOutcome> GetAll()
        => _outcomes.Values.ToList();
}

internal sealed class TeamTaskDispatcherExecutor(string id)
    : Executor<TeamTaskWorkflowInput, TeamTaskWorkflowInput>(id)
{
    public override ValueTask<TeamTaskWorkflowInput> HandleAsync(
        TeamTaskWorkflowInput message,
        IWorkflowContext context,
        CancellationToken cancellationToken = default)
        => ValueTask.FromResult(message);
}

internal sealed class TeamTaskExecutor(
    string id,
    TeamTaskDefinition task,
    IReadOnlyList<string> effectiveDependencies,
    ITeamTaskWorkflowRuntime runtime,
    TeamTaskOutcomeRegistry outcomeRegistry) : Executor<object, TeamTaskOutcome>(id)
{
    public override async ValueTask<TeamTaskOutcome> HandleAsync(
        object message,
        IWorkflowContext context,
        CancellationToken cancellationToken = default)
    {
        // 幂等保护：MAF Fan-in Barrier 可能对同一任务投递多条消息（每个上游产出触发一次），
        // 已记录过结果的执行体必须直接返回缓存结果，避免重复执行（重复工具副作用 / 重复 Agent 轮次）。
        if (outcomeRegistry.TryGet(task.Id, out var recorded))
            return recorded;

        var dependencyOutcomes = effectiveDependencies
            .Order(StringComparer.OrdinalIgnoreCase)
            .Select(outcomeRegistry.Get)
            .ToArray();
        var blockedDependencies = dependencyOutcomes.Where(outcome => !outcome.Success).ToArray();
        if (blockedDependencies.Length > 0)
        {
            return outcomeRegistry.Set(new TeamTaskOutcome(
                task.Id,
                task.Title,
                TeamTaskOutcomeStatus.Blocked,
                null,
                $"Blocked by upstream task(s): {string.Join(", ", blockedDependencies.Select(outcome => outcome.TaskId))}.",
                0,
                false,
                0));
        }

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var maxAttempts = ResolveMaxAttempts(task);
        var attemptKey = $"task:{task.Id}:attempt";
        var fingerprintKey = $"task:{task.Id}:errorFingerprint";
        var lastResult = (TeamRunResult?)null;
        var lastError = (string?)null;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            // Persist attempt count and error fingerprint into MAF Shared State so that
            // checkpoint recovery can observe how far the task progressed.
            await context.QueueStateUpdateAsync(
                attemptKey,
                BinaryData.FromString(attempt.ToString(CultureInfo.InvariantCulture)),
                cancellationToken).ConfigureAwait(false);

            try
            {
                lastResult = await runtime.ExecuteTaskAsync(task, cancellationToken).ConfigureAwait(false);
                var failed = lastResult.Error is not null || lastResult.HadFailures;
                if (!failed)
                {
                    stopwatch.Stop();
                    return outcomeRegistry.Set(new TeamTaskOutcome(
                        task.Id,
                        task.Title,
                        TeamTaskOutcomeStatus.Succeeded,
                        lastResult.Output,
                        null,
                        lastResult.TurnsCompleted,
                        lastResult.MaxTurnsReached,
                        stopwatch.ElapsedMilliseconds));
                }

                // Task failed. Record error fingerprint for observability and retry decision.
                lastError = lastResult.Error?.Detail ?? "Team task reported agent failures.";
                var fingerprint = ComputeErrorFingerprint(lastError);
                await context.QueueStateUpdateAsync(
                    fingerprintKey,
                    BinaryData.FromString(fingerprint),
                    cancellationToken).ConfigureAwait(false);

                // Write tasks (ToolPolicy.WriteAllowed) must not auto-retry side effects;
                // they rely on OperationId idempotency ledger for safe re-execution across
                // resume generations, not blind in-generation retry. A failed write task
                // is a terminal Failed (not Blocked) — the business layer decides whether
                // to roll back or initiate a new generation.
                if (task.ToolPolicy == TeamToolPolicy.WriteAllowed)
                {
                    stopwatch.Stop();
                    return outcomeRegistry.Set(new TeamTaskOutcome(
                        task.Id,
                        task.Title,
                        TeamTaskOutcomeStatus.Failed,
                        lastResult.Output,
                        lastError,
                        lastResult.TurnsCompleted,
                        lastResult.MaxTurnsReached,
                        stopwatch.ElapsedMilliseconds));
                }

                // Read-only tasks: consult RetryPolicy for transient failure retry.
                // RetryPolicy null or fingerprint not retryable → no retry, terminal Failed.
                if (task.RetryPolicy is null || !IsRetryable(fingerprint, task.RetryPolicy))
                {
                    stopwatch.Stop();
                    return outcomeRegistry.Set(new TeamTaskOutcome(
                        task.Id,
                        task.Title,
                        TeamTaskOutcomeStatus.Failed,
                        lastResult.Output,
                        lastError,
                        lastResult.TurnsCompleted,
                        lastResult.MaxTurnsReached,
                        stopwatch.ElapsedMilliseconds));
                }

                if (attempt < maxAttempts)
                {
                    var delay = ComputeBackoffDelay(task.RetryPolicy, attempt);
                    if (delay > TimeSpan.Zero)
                        // Known limitation: this Task.Delay blocks the current MAF Superstep. MAF 1.15.0
                        // provides no non-blocking Delay/Timer Activity, and the only non-blocking
                        // alternative would require extending TeamTaskOutcomeStatus (RetryableBlocked)
                        // and re-dispatching from TeamTaskDispatcherExecutor in a new Superstep.
                        // Impact is limited: read-only task backoff does not block sibling Executors
                        // that already started in the same Superstep. Tracked for future iteration.
                        await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // Preserve the active workflow/business run for a later resume.
                throw;
            }
        }

        stopwatch.Stop();
        // All attempts exhausted or non-retryable failure → Blocked (per T-03 §5: structured
        // Blocked result for OneCode domain policy to decide business failure).
        return outcomeRegistry.Set(new TeamTaskOutcome(
            task.Id,
            task.Title,
            TeamTaskOutcomeStatus.Blocked,
            lastResult?.Output,
            lastError ?? "Team task exhausted all retry attempts.",
            lastResult?.TurnsCompleted ?? 0,
            lastResult?.MaxTurnsReached ?? false,
            stopwatch.ElapsedMilliseconds));
    }

    private static int ResolveMaxAttempts(TeamTaskDefinition task)
    {
        // Write tasks: always 1 attempt — no blind retry of side effects.
        if (task.ToolPolicy == TeamToolPolicy.WriteAllowed)
            return 1;
        // RetryPolicy.MaxAttempts takes precedence over TeamTaskDefinition.MaxAttempts
        // because it carries the full strategy (delay/backoff/fingerprint filtering).
        if (task.RetryPolicy is { } policy)
            return Math.Max(1, policy.MaxAttempts);
        return Math.Max(1, task.MaxAttempts);
    }

    private static TimeSpan ComputeBackoffDelay(TaskRetryPolicy policy, int attempt)
    {
        if (policy.InitialDelay <= TimeSpan.Zero)
            return TimeSpan.Zero;
        var delayTicks = (long)(policy.InitialDelay.Ticks * Math.Pow(policy.BackoffMultiplier, attempt - 1));
        var capped = Math.Min(delayTicks, policy.MaxDelay.Ticks);
        return TimeSpan.FromTicks(capped);
    }

    private static string ComputeErrorFingerprint(string error)
        => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(error))).ToLowerInvariant()[..16];

    private static bool IsRetryable(string fingerprint, TaskRetryPolicy policy)
    {
        // No fingerprint filter → retry all transient failures.
        if (policy.RetryableErrorFingerprints is null or { Count: 0 })
            return true;
        return policy.RetryableErrorFingerprints.Contains(fingerprint, StringComparer.OrdinalIgnoreCase);
    }
}
