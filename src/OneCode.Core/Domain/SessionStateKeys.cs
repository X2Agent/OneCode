namespace OneCode.Core.Domain;

/// <summary>
/// AgentSession.StateBag 的 key 常量。
///
/// 提供所有 StateBag key 的单一事实源，避免字符串拼写错误（运行时才暴露）。
/// 命名约定：<c>{模块}.{字段名}</c>，分层隔离。
/// </summary>
public static class SessionStateKeys
{
    /// <summary>当前 Agent 状态（Active/Recovering/Blocked）。</summary>
    public const string CurrentState = "state_machine.current_state";

    /// <summary>连续失败计数（由 StateMachineMiddleware 独占递增）。</summary>
    public const string ConsecutiveFailures = "state_machine.consecutive_failures";

    /// <summary>累计工具调用次数。</summary>
    public const string TotalToolCalls = "state_machine.total_tool_calls";

    /// <summary>最近工具调用记录（FixedSizeRingBuffer，容量 50）。</summary>
    public const string RecentToolCalls = "state_machine.recent_tool_calls";

    /// <summary>已修改文件路径集合（HashSet，OrdinalIgnoreCase）。</summary>
    public const string ModifiedFiles = "state_machine.modified_files";

    /// <summary>自上次 build 以来的编辑次数（由 VerificationMiddleware 独占递增）。</summary>
    public const string EditsSinceLastBuild = "verification.edits_since_last_build";

    /// <summary>
    /// 结构化工具执行上下文（IsError + GuidanceKind）。
    /// per-call 在 ToolResultUnwrapMiddleware pre 部分重置。
    /// </summary>
    public const string ToolExecutionContext = "tool.execution_context";
}
