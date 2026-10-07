using OneCode.Core.Domain;
using OneCode.Core.Errors;

namespace OneCode.Core.Tools;

public interface IAgentRunner
{
    Task<AgentRunResult> RunAsync(AgentRunRequest request, CancellationToken ct = default);
}

public sealed record AgentRunRequest(
    string Prompt,
    string Agent,
    string? Description = null,
    int? MaxTurns = null,
    CacheSafeParams? CacheSafeParams = null,
    ToolCapabilitySet? ParentCapabilities = null,
    // 调用方预建的任务 ID（AgentTool background 模式）。非空时 WorkerAgentService
    // 更新该任务而非新建，避免任务列表出现重复条目。
    string? TaskId = null,
    // 自定义代理（.onecode/agents/*.md）：Instructions 覆盖内置角色 overlay；
    // AllowedToolNames 与父级能力取交集（只减不增）。两者为空即按内置代理处理。
    string? Instructions = null,
    IReadOnlyList<string>? AllowedToolNames = null,
    // 工作目录覆盖（如 GOAL 隔离 worktree）。null 时 fork 使用会话工作目录访问器。
    string? WorkingDirectory = null,
    // fork 级账本接线（GOAL 步内并行）：把 fork 的文件写入 intent 挂到 step 级 run 账本下，
    // 崩溃后新世代的 ReconcileRunAsync 才能回滚孤儿写入。非 GOAL 路径为 null。
    ForkLedgerScope? LedgerScope = null);

/// <summary>
/// fork 级账本事务的作用域：同一 run 账本前缀 + 同一围栏令牌，每个 fork 一个
/// operationId。fork 侧只做 Begin + PersistTo + Commit，不做 ReconcileRunAsync——
/// 并发 fork 各自 reconcile 会回滚彼此刚持久化的 intent，世代清扫由 step 级独占。
/// </summary>
public sealed record ForkLedgerScope(
    OneCode.Core.Workflows.IOperationLedger Ledger,
    string RunLedgerId,
    string OperationId,
    long FencingToken)
{
    /// <summary>派生 fork 级事务标识（operationId 追加任务段，账本前缀与围栏不变）。</summary>
    public ForkLedgerScope ForTask(string taskId) => this with { OperationId = $"{OperationId}/task/{taskId}" };
}

/// <summary>
/// 子 Agent 执行结果。<see cref="Error"/> 非 null 表示执行失败（异常已捕获并转换为
/// <see cref="AgentProblemDetails"/>）。消费方应优先检查此字段以透传结构化错误。
/// </summary>
public sealed record AgentRunResult(
    string Agent,
    SessionId ConversationId,
    string? Output,
    int TurnsCompleted,
    bool MaxTurnsReached,
    AgentProblemDetails? Error = null,
    // fork 事务提交的变更文件与 token 消耗（GOAL 步内 DAG 的证据与预算依赖此透传）。
    IReadOnlyList<string>? ChangedFiles = null,
    long InputTokens = 0,
    long OutputTokens = 0);
