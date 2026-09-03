using Microsoft.Agents.AI.Workflows;
using OneCode.App.Services.Agent;

namespace OneCode.App.Services.Runtime;

/// <summary>统一模式 Workflow Host 的单次执行产物。</summary>
public sealed record ModeWorkflowRunResult<TOutput>(
    DurableWorkflowRunResult Durable,
    TOutput Output);

/// <summary>
/// 每模式一份 Workflow Host 策略（ModePolicy 的 host 层）。
/// Compile / checkpoint 命令 / lease 回调 / 终态解析保留各模式语义；MAF 编译器不合一（ADR §3）。
/// 审批门机制按模式自持（Build 流内门 / Plan AggregateApprovalGate / Team RequestPortGate），
/// 语义保留不强行统一（不合并四模式审批门）。
/// </summary>
/// <param name="RunId">Registry generation 查询键（与编译产物 Registration.RunId 一致，如 <c>goal/{id}</c>）。</param>
/// <param name="Compile">由执行世代产出编译产物；Registration 必须取自编译器，避免 registry 身份漂移。</param>
/// <param name="SerializerOptions">Checkpoint 序列化选项（各模式现有选项，不改）。</param>
/// <param name="EventSink">运行时事件回调（透传 TUI/编排层）。</param>
/// <param name="LeaseAcquired">lease 回调：claim 业务聚合并绑定运行时（fencing 对账在各模式内完成）。</param>
/// <param name="TerminalStateResolver">由运行时事件解析 Registry 终态；null 表示交由 durable host 默认。</param>
/// <param name="DisplayName">异常消息用的模式显示名（保持各模式既有消息文本）。</param>
public sealed record ModeWorkflowPolicy<TInput>(
    string RunId,
    Func<int, ModeWorkflowCompiled<TInput>> Compile,
    JsonSerializerOptions SerializerOptions,
    Func<WorkflowRuntimeEvent, CancellationToken, ValueTask>? EventSink = null,
    Func<WorkflowRunRecord, CancellationToken, ValueTask>? LeaseAcquired = null,
    Func<IReadOnlyList<WorkflowRuntimeEvent>, WorkflowRunState?>? TerminalStateResolver = null,
    string DisplayName = "Mode workflow")
    where TInput : notnull;

/// <summary>一次编译的产物：Registration / Workflow / Input / checkpoint 命令标识。</summary>
public sealed record ModeWorkflowCompiled<TInput>(
    WorkflowRunRegistration Registration,
    Workflow Workflow,
    TInput Input,
    string CommandId)
    where TInput : notnull;

/// <summary>
/// 统一模式 Workflow Host 骨架（以 Goal 已验证形态泛化）：
/// generation 递增（registry 对账）→ MAF durable 执行（lease/终态钩子）→ typed 输出抽取。
/// Build/Goal/Team Host 的逐方法同构三件套由本类承担（Team 随 Stage 4d 接入）；
/// 领域执行器与 MAF 编译器不合一。
/// </summary>
public sealed class ModeWorkflowHost(
    IDurableWorkflowHost durableHost,
    IWorkflowRunRegistry workflowRunRegistry)
{
    /// <summary>开启新执行世代：generation = 磁盘 ExecutionGeneration + 1，执行并抽取 typed 输出。</summary>
    public async Task<ModeWorkflowRunResult<TOutput>> RunNextAsync<TInput, TOutput>(
        ModeWorkflowPolicy<TInput> policy,
        CancellationToken ct = default)
        where TInput : notnull
        where TOutput : class
    {
        var generation = await LoadGenerationAsync(policy.RunId, ct).ConfigureAwait(false);
        return await RunAsync<TInput, TOutput>(policy, generation, ct).ConfigureAwait(false);
    }

    /// <summary>以显式执行世代运行（恢复/续跑路径）并抽取 typed 输出。</summary>
    public async Task<ModeWorkflowRunResult<TOutput>> RunAsync<TInput, TOutput>(
        ModeWorkflowPolicy<TInput> policy,
        int executionGeneration,
        CancellationToken ct = default)
        where TInput : notnull
        where TOutput : class
    {
        var durable = await RunAsync(policy, executionGeneration, ct).ConfigureAwait(false);
        return new ModeWorkflowRunResult<TOutput>(durable, ExtractOutput<TOutput>(durable, policy.DisplayName, policy.RunId));
    }

    /// <summary>以显式执行世代运行，不要求 typed 输出（Build 显式 attempt 路径的既有语义）。</summary>
    public async Task<DurableWorkflowRunResult> RunAsync<TInput>(
        ModeWorkflowPolicy<TInput> policy,
        int executionGeneration,
        CancellationToken ct = default)
        where TInput : notnull
    {
        var definition = policy.Compile(executionGeneration);
        return await durableHost.RunAsync(
            definition.Registration,
            definition.Workflow,
            definition.Input,
            definition.CommandId,
            policy.SerializerOptions,
            policy.EventSink,
            executionGeneration,
            policy.LeaseAcquired,
            policy.TerminalStateResolver,
            ct: ct).ConfigureAwait(false);
    }

    private static TOutput ExtractOutput<TOutput>(
        DurableWorkflowRunResult durable,
        string displayName,
        string runId)
        where TOutput : class
        => durable.Events
            .OfType<WorkflowRuntimeEvent.Output>()
            .Select(item => item.Value)
            .OfType<TOutput>()
            .SingleOrDefault()
        ?? throw new InvalidOperationException(
            $"{displayName} '{runId}' produced no typed output.");

    private async Task<int> LoadGenerationAsync(string runId, CancellationToken ct)
    {
        var existing = await workflowRunRegistry.LoadAsync(runId, ct).ConfigureAwait(false);
        return (existing?.ExecutionGeneration ?? 0) + 1;
    }
}
