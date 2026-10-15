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
    // 派工点解析的轮数上限（自定义代理 frontmatter max_turns）。与 MaxTurns（调用方请求的
    // 上限，只降不升）分离：runner 取 min(请求值, 上限链)，上限链 = 本值 > settings > profile 默认。
    int? MaxTurnsCeiling = null,
    // 工作目录覆盖（如 GOAL 隔离 worktree）。null 时 fork 使用会话工作目录访问器。
    string? WorkingDirectory = null);

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
    long OutputTokens = 0,
    // 工具调用总数（结果摘要行 "N tool calls" 的数据源；历史调用方默认 0 保持兼容）。
    int ToolCallCount = 0);
