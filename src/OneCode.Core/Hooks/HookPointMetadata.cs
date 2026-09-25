namespace OneCode.Core.Hooks;

/// <summary>
/// 单个拦截点的元数据：显示名、语义说明与可选 matcher 声明。
/// matcher 语义（实际比较值来源）只在这里描述一次，其余文档（docs/hooks.md）从本注册表同步。
/// </summary>
/// <param name="Name">拦截点线格式名（协议名）。</param>
/// <param name="DisplayName">展示名（中文，用于 /hooks 与文档）。</param>
/// <param name="Description">拦截点触发时机与语义说明（中文）。</param>
/// <param name="Matcher">可选 matcher 声明；null 表示该拦截点不做 matcher 过滤。</param>
public sealed record HookPointMetadata(
    string Name,
    string DisplayName,
    string Description,
    HookMatcherMetadata? Matcher)
{
    /// <summary>带 matcher 声明的元数据。</summary>
    public static HookPointMetadata WithMatcher(string name, string displayName, string description, HookMatcherMetadata matcher) =>
        new(name, displayName, description, matcher);

    /// <summary>无 matcher 的元数据（触发即命中所有已配置 hook）。</summary>
    public static HookPointMetadata WithoutMatcher(string name, string displayName, string description) =>
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

/// <summary>hook 拦截点元数据注册表——拦截点语义与 matcher 的唯一权威来源。</summary>
public static class HookPointMetadataRegistry
{
    /// <summary>对用户 DSL 开放的拦截点元数据，按协议执行顺序排列。</summary>
    public static IReadOnlyList<HookPointMetadata> All { get; } =
    [
        HookPointMetadata.WithoutMatcher(
            HookInterceptionPoints.ToWireName(HookInterceptionPoint.Input),
            "外部输入",
            "外部输入进入 agent run 前触发。deny 阻止本轮输入进入运行循环（不写会话历史），每条 hook 的 AdditionalContext 并入本轮输入。"),

        HookPointMetadata.WithMatcher(
            HookInterceptionPoints.ToWireName(HookInterceptionPoint.PreModelCall),
            "模型请求前",
            "在一次模型请求发送前触发（纯审计）。上下文含请求消息与模型标识；裁决结果不阻断模型调用。",
            new HookMatcherMetadata(
                HookInterceptionPoints.MatcherField(HookInterceptionPoint.PreModelCall)!,
                "按模型标识匹配，大小写不敏感，支持 glob；缺省/空值匹配全部模型。",
                ["gpt-*", "claude-*", "*"])),

        HookPointMetadata.WithMatcher(
            HookInterceptionPoints.ToWireName(HookInterceptionPoint.PostModelCall),
            "模型响应后",
            "在一次模型完整响应返回后触发（工具调用执行前，纯审计）。上下文含响应内容与完成原因；裁决结果不改变主流程。",
            new HookMatcherMetadata(
                HookInterceptionPoints.MatcherField(HookInterceptionPoint.PostModelCall)!,
                "按模型标识匹配，规则同 pre_model_call。",
                ["gpt-*", "claude-*", "*"])),

        HookPointMetadata.WithMatcher(
            HookInterceptionPoints.ToWireName(HookInterceptionPoint.PreToolCall),
            "工具调用前",
            "在工具真正执行前触发。deny 使本次工具调用以错误载荷返回、批次循环继续（不中断整轮）。",
            new HookMatcherMetadata(
                HookInterceptionPoints.MatcherField(HookInterceptionPoint.PreToolCall)!,
                "按工具名匹配（来自工具能力注册表），大小写不敏感，支持 glob（如 Write、mcp__*）；缺省/空值匹配全部工具。",
                ["Bash", "Edit", "Write", "Read", "Grep", "Glob", "Task", "todos_*", "mcp__*"])),

        HookPointMetadata.WithMatcher(
            HookInterceptionPoints.ToWireName(HookInterceptionPoint.PostToolCall),
            "工具调用后",
            "在工具成功或失败返回后触发。上下文含工具结果与错误内容；审计/通知语义，失败不影响已产生的工具结果。",
            new HookMatcherMetadata(
                HookInterceptionPoints.MatcherField(HookInterceptionPoint.PostToolCall)!,
                "按工具名匹配，规则同 pre_tool_call。",
                ["Bash", "Edit", "Write", "Read", "Grep", "Glob", "Task", "todos_*", "mcp__*"])),

        HookPointMetadata.WithoutMatcher(
            HookInterceptionPoints.ToWireName(HookInterceptionPoint.Output),
            "最终输出",
            "在最终响应交付调用方前触发。deny 拦截本次响应；有该节点 hook 时框架缓冲响应后再裁决，无 hook 时不影响流式体验。"),
    ];
}
