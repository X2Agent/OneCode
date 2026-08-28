namespace OneCode.Core.Hooks;

/// <summary>
/// 单个 hook 事件的元数据：显示名、语义说明与可选 matcher 声明。
/// matcher 语义（实际比较值来源）只在这里描述一次，其余文档（docs/hooks.md）从本注册表同步。
/// </summary>
/// <param name="Name">事件名（枚举名）。</param>
/// <param name="DisplayName">展示名（中文，用于 /hooks 与文档）。</param>
/// <param name="Description">事件触发时机与语义说明（中文）。</param>
/// <param name="Matcher">可选 matcher 声明；null 表示该事件不做 matcher 过滤。</param>
public sealed record HookEventMetadata(
    string Name,
    string DisplayName,
    string Description,
    HookMatcherMetadata? Matcher)
{
    /// <summary>带 matcher 声明的元数据。</summary>
    public static HookEventMetadata WithMatcher(string name, string displayName, string description, HookMatcherMetadata matcher) =>
        new(name, displayName, description, matcher);

    /// <summary>无 matcher 的元数据（触发即命中所有已配置 hook）。</summary>
    public static HookEventMetadata WithoutMatcher(string name, string displayName, string description) =>
        new(name, displayName, description, null);
}

/// <summary>
/// matcher 元数据：说明字段 + 语义说明 + 当前实际会出现的取值（不含通配）。
/// </summary>
/// <param name="PayloadField">参与匹配的字段名（人类可读说明用）。</param>
/// <param name="Description">matcher 语义说明：实际比较值从哪里来、大小写与通配规则。</param>
/// <param name="KnownValues">当前实现中实际会出现（或文档承诺将来会出现）的取值。</param>
public sealed record HookMatcherMetadata(
    string PayloadField,
    string Description,
    IReadOnlyList<string> KnownValues);

/// <summary>hook 事件元数据注册表——事件语义与 matcher 的唯一权威来源。</summary>
public static class HookEventMetadataRegistry
{
    /// <summary>全部事件的元数据，按枚举定义顺序排列。</summary>
    public static IReadOnlyList<HookEventMetadata> All { get; } =
    [
        HookEventMetadata.WithMatcher(
            nameof(HookEvent.PreToolUse),
            "工具调用前",
            "在工具真正执行前触发。exit code 2 阻断本次工具调用（BlockingErrors 首条为阻断原因），其他非零 exit code 仅记录警告。",
            new HookMatcherMetadata(
                nameof(HookPayload.ToolName),
                "按工具名匹配（来自工具能力注册表），大小写不敏感，支持 glob（如 Write、mcp.*）；缺省/空值匹配全部工具。",
                ["Bash", "Edit", "Write", "Read", "Grep", "Glob", "Task", "TodoWrite", "mcp.*"])),

        HookEventMetadata.WithMatcher(
            nameof(HookEvent.PostToolUse),
            "工具调用后",
            "在工具成功执行后触发。可注入附加上下文（AdditionalContexts）并入后续输入，无法回滚工具结果。",
            new HookMatcherMetadata(
                nameof(HookPayload.ToolName),
                "按工具名匹配，规则同 PreToolUse。",
                ["Bash", "Edit", "Write", "Read", "Grep", "Glob", "Task", "TodoWrite", "mcp.*"])),

        HookEventMetadata.WithoutMatcher(
            nameof(HookEvent.UserPromptSubmit),
            "用户提交 prompt",
            "在用户提交 prompt 后、进入 agent 运行循环前触发（会话内每轮提问均触发，Stop 纠偏续跑不触发）。exit code 2 阻断本轮 prompt（不再进入运行循环，也不落会话历史），AdditionalContexts 并入本轮输入。"),

        HookEventMetadata.WithMatcher(
            nameof(HookEvent.SessionStart),
            "会话开始",
            "会话生命周期开始时触发（清空上下文后同样触发）。",
            new HookMatcherMetadata(
                "source",
                "按会话启动来源匹配，大小写不敏感，支持 glob；缺省/空值匹配全部来源。",
                ["startup", "resume", "switch"])),

        HookEventMetadata.WithMatcher(
            nameof(HookEvent.SessionEnd),
            "会话结束",
            "会话生命周期结束时触发（/close 显式关闭、用户输入退出（/exit）或宿主停止兜底）。",
            new HookMatcherMetadata(
                "reason",
                "按会话结束原因匹配，大小写不敏感，支持 glob；缺省/空值匹配全部原因。",
                [SessionEndReason.Close, SessionEndReason.PromptInputExit, SessionEndReason.Other])),

        HookEventMetadata.WithMatcher(
            nameof(HookEvent.Stop),
            "主循环终结",
            "agent 主循环终结时触发（无论成功完成还是异常收场），终结收尾前触发。exit code 2 可阻断终结并触发纠偏续跑（连续阻断有上限，超限降级为警告；durable Build 尝试不支持重入，直接降级为警告）。",
            new HookMatcherMetadata(
                nameof(HookPayload.TerminalReason),
                "按终结原因（BuildTerminalReason 枚举名）匹配，大小写不敏感，支持 glob；缺省/空值匹配全部终结原因。",
                ["Completed", "TurnLimitReached", "BudgetExceeded", "Cancelled", "ValidationFailed", "AgentException", "PermissionRefused", "ClarificationRequired", "Blocked"])),

        HookEventMetadata.WithMatcher(
            nameof(HookEvent.StopFailure),
            "运行异常中断",
            "运行因未处理异常中断时触发（在错误事件下发与失败通知之前）。无阻断语义——hook 失败只记录日志。",
            new HookMatcherMetadata(
                nameof(HookPayload.ErrorCategory),
                "按异常类别匹配，大小写不敏感，支持 glob；缺省/空值匹配全部类别。",
                ["rate_limit", "auth_failed", "billing", "invalid_request", "server_error", "max_output_tokens", "unknown"])),

        HookEventMetadata.WithMatcher(
            nameof(HookEvent.Notification),
            "通知",
            "系统通知事件（当前实现：权限审批挂起时，在 TUI 审批请求下发前触发）。无阻断语义。",
            new HookMatcherMetadata(
                "notification_type",
                "按通知类型匹配，大小写不敏感，支持 glob；缺省/空值匹配全部类型。",
                ["permission_prompt"])),

        HookEventMetadata.WithMatcher(
            nameof(HookEvent.PreCompact),
            "压缩前",
            "上下文压缩开始前触发。",
            new HookMatcherMetadata(
                nameof(HookPayload.Trigger),
                "按压缩触发方式匹配，大小写不敏感，支持 glob；缺省/空值匹配全部触发方式。",
                [HookTriggers.Manual, HookTriggers.Auto])),

        HookEventMetadata.WithMatcher(
            nameof(HookEvent.PostCompact),
            "压缩后",
            "上下文压缩完成后触发（ToolResponse 携带摘要）。可注入附加上下文（AdditionalContexts）。",
            new HookMatcherMetadata(
                nameof(HookPayload.Trigger),
                "按压缩触发方式匹配，规则同 PreCompact。",
                [HookTriggers.Manual, HookTriggers.Auto])),

        HookEventMetadata.WithMatcher(
            nameof(HookEvent.GoalStageInvoke),
            "Goal 阶段调用",
            "Goal 工作流阶段（目标拆解 / 子目标拆解 / 目标重规划）调用前触发。无阻断语义。",
            new HookMatcherMetadata(
                "stage",
                "按阶段名匹配，大小写不敏感，支持 glob；缺省/空值匹配全部阶段。",
                ["goal-decomposer", "goal-sub-decomposer", "goal-replanner"])),
    ];
}
