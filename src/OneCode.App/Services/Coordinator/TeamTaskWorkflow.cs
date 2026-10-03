using Microsoft.Agents.AI.Workflows;
using OneCode.App.Services.Agent;
using OneCode.Core.Coordinator;

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
    public TeamTaskWorkflowDefinition Compile(
        TeamRun run,
        TeamConfig config,
        string modelId,
        ITeamTaskWorkflowRuntime runtime,
        JsonSerializerOptions? serializerOptions = null)
    {
        var plan = run.Plan ?? throw new InvalidOperationException($"TeamRun '{run.Id}' has no approved plan.");
        TeamWorkflowTopology.Validate(plan.Tasks);

        var orderedTasks = WorkflowTopology.KahnOrder(
            plan.Tasks, task => task.Id, task => task.DependsOn,
            "Team task graph contains a dependency cycle.");
        var effectiveDependencies = TeamWorkflowTopology.BuildEffectiveDependencies(orderedTasks);
        var outcomeRegistry = new TeamTaskOutcomeRegistry();
        var dispatcher = new TeamTaskDispatcherExecutor(TeamWorkflowTopology.DispatcherId);
        var taskExecutors = orderedTasks.ToDictionary(
            task => task.Id,
            task => (ExecutorBinding)new TeamTaskExecutor(
                TeamWorkflowTopology.ExecutorIdPrefix + TeamWorkflowTopology.NormalizeId(task.Id),
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
        // W3-D: single-root / single-task graphs skip FanOut — plain edge keeps durable IDs stable.
        if (roots.Length == 1)
            builder.AddEdge(dispatcher, roots[0], "dispatch-single", false);
        else
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
        var definitionHash = TeamWorkflowTopology.ComputeDefinitionHash(
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

    /// <summary>校验已批准的 Team 任务图（ID 唯一、依赖存在且无环）。</summary>
    internal static void Validate(IReadOnlyList<TeamTaskDefinition> tasks)
        => TeamWorkflowTopology.Validate(tasks);
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
                        // Known limitation: this Task.Delay blocks the current MAF Superstep. MAF 1.19.0
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
        // All attempts exhausted or non-retryable failure → Blocked.
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
