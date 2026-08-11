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
    string? TaskId = null);

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
    AgentProblemDetails? Error = null);
