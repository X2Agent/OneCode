using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Agents.AI.Workflows;
using OneCode.App.Services.Agent;
using OneCode.App.Services.Runtime;
using OneCode.Core.Build;

namespace OneCode.App.Services.BuildMode;

public sealed record ControlledBuildAttemptInput(
    string WorkflowRunId,
    BuildRunId BuildRunId,
    int Attempt,
    string OperationId);

public sealed record ControlledBuildAttemptOutput(
    BuildRunId BuildRunId,
    int Attempt,
    string OperationId,
    MainAgentRunResult Result);

public sealed record ControlledBuildAttemptDefinition(
    Workflow Workflow,
    WorkflowRunRegistration Registration,
    ControlledBuildAttemptInput Input);

public sealed record ControlledBuildAttemptRunResult(
    DurableWorkflowRunResult Durable,
    ControlledBuildAttemptOutput Output);

/// <summary>
/// Process-local implementation of one idempotent Build attempt. It may own MainAgentRunner,
/// EditTransaction and UI callbacks, none of which are serialized into a MAF checkpoint.
/// </summary>
public interface IControlledBuildAttemptRuntime
{
    Task<MainAgentRunResult> ExecuteAsync(
        ControlledBuildAttemptInput input,
        CancellationToken ct = default);
}

public sealed class ControlledBuildAttemptWorkflowCompiler
{
    private const string ContractVersion = "controlled-build-attempt-v1";
    private const string ExecutorId = "controlled-build-attempt-executor-v1";
    private const string WorkflowName = "controlled-build-attempt-workflow-v1";

    public ControlledBuildAttemptDefinition Compile(
        BuildRun buildRun,
        int attempt,
        string modelId,
        string systemPrompt,
        string toolCapabilityHash,
        IControlledBuildAttemptRuntime runtime,
        JsonSerializerOptions? serializerOptions = null)
    {
        ArgumentNullException.ThrowIfNull(buildRun);
        ArgumentNullException.ThrowIfNull(runtime);
        if (buildRun.State is not (BuildRunState.Implementing or BuildRunState.Verifying or BuildRunState.Recovering))
            throw new InvalidOperationException($"BuildRun '{buildRun.Id}' is not ready for an execution attempt.");
        if (attempt <= 0)
            throw new ArgumentOutOfRangeException(nameof(attempt));
        if (string.IsNullOrWhiteSpace(modelId))
            throw new ArgumentException("ModelId is required.", nameof(modelId));
        if (string.IsNullOrWhiteSpace(systemPrompt))
            throw new ArgumentException("SystemPrompt is required.", nameof(systemPrompt));
        if (string.IsNullOrWhiteSpace(toolCapabilityHash))
            throw new ArgumentException("Tool capability hash is required.", nameof(toolCapabilityHash));

        var definitionHash = ComputeDefinitionHash(
            buildRun,
            modelId,
            systemPrompt,
            toolCapabilityHash,
            serializerOptions);
        var workflowRunId = $"build/{buildRun.Id}";
        var operationId = $"{workflowRunId}/attempt/{attempt}/agent-edit-transaction";
        var input = new ControlledBuildAttemptInput(workflowRunId, buildRun.Id, attempt, operationId);
        var executor = new ControlledBuildAttemptExecutor(ExecutorId, runtime);
        var workflow = new WorkflowBuilder(executor)
            .WithName(WorkflowName)
            .WithOutputFrom(executor)
            .Build(validateOrphans: true);
        return new ControlledBuildAttemptDefinition(
            workflow,
            new WorkflowRunRegistration(workflowRunId, "controlled-build-attempt", definitionHash),
            input);
    }

    public static string ComputeToolCapabilityHash(
        ToolCapabilitySet? capabilities,
        IEnumerable<string> toolNames)
    {
        var canonical = new
        {
            allowedToolNames = (capabilities?.AllowedToolNames ?? [])
                .Order(StringComparer.OrdinalIgnoreCase).ToArray(),
            allowedCategories = (int?)capabilities?.AllowedCategories,
            maximumRisk = (int?)capabilities?.MaximumRisk,
            capabilities?.AllowDynamicActivation,
            capabilities?.AllowSubAgents,
            activeToolNames = toolNames
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase).ToArray(),
        };
        return Hash(JsonSerializer.Serialize(canonical));
    }

    /// <summary>
    /// Derives the attempt capability boundary from the approved tool policy. The approved policy
    /// is the authoritative source for DefinitionHash determinism, not the ambient activation
    /// context, which may differ between plan approval and the attempt run.
    /// </summary>
    public static ToolCapabilitySet ApprovedPolicyCapabilities(
        BuildRun buildRun,
        ToolCapabilitySet? ambientCapabilities)
    {
        var approvedNames = buildRun.ApprovedToolPolicy?.ToolNames
            ?? throw new InvalidOperationException(
                $"BuildRun '{buildRun.Id}' has no approved tool policy before its attempt.");
        var approvedSet = approvedNames.ToImmutableHashSet(StringComparer.OrdinalIgnoreCase);
        return ambientCapabilities is null
            ? ToolCapabilitySet.CreateUnrestricted(approvedNames)
            : ambientCapabilities with { AllowedToolNames = approvedSet };
    }

    internal static string ComputeDefinitionHash(
        BuildRun run,
        string modelId,
        string systemPrompt,
        string toolCapabilityHash,
        JsonSerializerOptions? serializerOptions = null)
    {
        var canonical = new
        {
            contractVersion = ContractVersion,
            workflowName = WorkflowName,
            executorId = ExecutorId,
            buildRunId = run.Id.ToString(),
            modelId,
            systemPromptHash = Hash(systemPrompt),
            toolCapabilityHash,
            // Checkpoint 序列化契约纳入恢复凭据（S-06）：序列化配置变化必须改变 Hash，
            // 使 Registry 校验 fail-closed，避免用旧 checkpoint 以新契约反序列化。
            serializerOptions = serializerOptions is null
                ? "default"
                : JsonSerializer.Serialize(serializerOptions),
            plan = run.Plan is null
                ? null
                : new
                {
                    run.Plan.Summary,
                    tasks = run.Plan.Tasks
                        .OrderBy(task => task.Id, StringComparer.OrdinalIgnoreCase)
                        .Select(task => new
                        {
                            task.Id,
                            task.Title,
                            task.Description,
                            dependsOn = task.DependsOn.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
                            expectedFiles = task.ExpectedFiles.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
                            acceptance = task.AcceptanceCriteria.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
                        }).ToArray(),
                    validationCommands = run.Plan.ValidationCommands.ToArray(),
                    risks = run.Plan.Risks.Order(StringComparer.Ordinal).ToArray(),
                    nonGoals = run.Plan.NonGoals.Order(StringComparer.Ordinal).ToArray(),
                    run.Plan.RequireExplicitTaskCompletion,
                },
            scope = run.Scope is null
                ? null
                : new
                {
                    run.Scope.Goal,
                    inScope = run.Scope.InScope.Order(StringComparer.Ordinal).ToArray(),
                    outOfScope = run.Scope.OutOfScope.Order(StringComparer.Ordinal).ToArray(),
                    constraints = run.Scope.Constraints.Order(StringComparer.Ordinal).ToArray(),
                    acceptance = run.Scope.AcceptanceCriteria
                        .OrderBy(item => item.Id, StringComparer.Ordinal)
                        .Select(item => new { item.Id, item.Description, item.Required }).ToArray(),
                },
            workingDirectory = run.WorkingDirectory,
            run.WorkspaceFingerprint,
        };
        return Hash(JsonSerializer.Serialize(canonical));
    }

    private static string Hash(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private sealed class ControlledBuildAttemptExecutor(
        string id,
        IControlledBuildAttemptRuntime runtime)
        : Executor<ControlledBuildAttemptInput, ControlledBuildAttemptOutput>(id)
    {
        public override async ValueTask<ControlledBuildAttemptOutput> HandleAsync(
            ControlledBuildAttemptInput message,
            IWorkflowContext context,
            CancellationToken cancellationToken = default)
        {
            var result = await runtime.ExecuteAsync(message, cancellationToken).ConfigureAwait(false);
            return new ControlledBuildAttemptOutput(
                message.BuildRunId,
                message.Attempt,
                message.OperationId,
                result);
        }
    }
}

/// <summary>
/// Build 模式受控 attempt Workflow Host。执行骨架（generation/attempt 递增、durable 执行、
/// typed 输出抽取）由 <see cref="ModeWorkflowHost"/> 统一承担；本类保留 Build 语义：
/// attempt 依赖世代的编译、checkpoint 命令取编译产物 OperationId、
/// lease 回调（状态前置校验 → claim → PrepareAttemptAsync → Implementing 校验），
/// 显式 attempt 路径不要求 typed 输出。
/// </summary>
public sealed class ControlledBuildAttemptHost(
    IDurableWorkflowHost durableHost,
    ControlledBuildAttemptWorkflowCompiler compiler,
    IBuildRunStore buildRunStore,
    IWorkflowRunRegistry workflowRunRegistry,
    IBuildRunCoordinator buildRunCoordinator)
{
    private readonly ModeWorkflowHost _modeHost = new(durableHost, workflowRunRegistry);

    public async Task<ControlledBuildAttemptRunResult> RunNextAsync(
        BuildRun buildRun,
        string modelId,
        string systemPrompt,
        string toolCapabilityHash,
        IControlledBuildAttemptRuntime runtime,
        JsonSerializerOptions serializerOptions,
        Func<WorkflowRuntimeEvent, CancellationToken, ValueTask>? eventSink = null,
        CancellationToken ct = default)
    {
        var result = await _modeHost.RunNextAsync<ControlledBuildAttemptInput, ControlledBuildAttemptOutput>(
            CreatePolicy(buildRun, modelId, systemPrompt, toolCapabilityHash, runtime, serializerOptions, eventSink),
            ct).ConfigureAwait(false);
        return new ControlledBuildAttemptRunResult(result.Durable, result.Output);
    }

    public Task<DurableWorkflowRunResult> RunAsync(
        BuildRun buildRun,
        int attempt,
        string modelId,
        string systemPrompt,
        string toolCapabilityHash,
        IControlledBuildAttemptRuntime runtime,
        JsonSerializerOptions serializerOptions,
        Func<WorkflowRuntimeEvent, CancellationToken, ValueTask>? eventSink = null,
        CancellationToken ct = default)
        => _modeHost.RunAsync<ControlledBuildAttemptInput>(
            CreatePolicy(buildRun, modelId, systemPrompt, toolCapabilityHash, runtime, serializerOptions, eventSink),
            attempt,
            ct);

    private ModeWorkflowPolicy<ControlledBuildAttemptInput> CreatePolicy(
        BuildRun buildRun,
        string modelId,
        string systemPrompt,
        string toolCapabilityHash,
        IControlledBuildAttemptRuntime runtime,
        JsonSerializerOptions serializerOptions,
        Func<WorkflowRuntimeEvent, CancellationToken, ValueTask>? eventSink)
        => new(
            $"build/{buildRun.Id}",
            attempt =>
            {
                var definition = compiler.Compile(
                    buildRun,
                    attempt,
                    modelId,
                    systemPrompt,
                    toolCapabilityHash,
                    runtime,
                    serializerOptions);
                return new ModeWorkflowCompiled<ControlledBuildAttemptInput>(
                    definition.Registration,
                    definition.Workflow,
                    definition.Input,
                    definition.Input.OperationId);
            },
            serializerOptions,
            EventSink: eventSink,
            LeaseAcquired: async (workflowRun, callbackCt) =>
            {
                var current = await buildRunStore.LoadByIdAsync(buildRun.Id, callbackCt).ConfigureAwait(false)
                    ?? throw new InvalidOperationException($"BuildRun '{buildRun.Id}' was not found.");
                if (current.State is not (BuildRunState.Implementing or BuildRunState.Verifying or BuildRunState.Recovering))
                {
                    throw new InvalidOperationException(
                        $"BuildRun '{buildRun.Id}' cannot start an attempt from state '{current.State}'.");
                }
                _ = await buildRunStore.ClaimWorkflowAsync(
                    buildRun.Id,
                    workflowRun.FencingToken,
                    current.Version,
                    callbackCt).ConfigureAwait(false);
                var prepared = await buildRunCoordinator.PrepareAttemptAsync(
                    buildRun.Id,
                    workflowRun.FencingToken,
                    callbackCt).ConfigureAwait(false);
                if (prepared.State != BuildRunState.Implementing)
                {
                    throw new InvalidOperationException(
                        $"BuildRun '{buildRun.Id}' did not enter Implementing after attempt preparation.");
                }
            },
            DisplayName: "Controlled Build attempt");
}
