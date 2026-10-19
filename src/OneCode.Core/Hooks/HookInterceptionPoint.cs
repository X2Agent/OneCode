using System.Text.Json.Serialization;

namespace OneCode.Core.Hooks;

/// <summary>
/// Hook 拦截点——对齐 AGENT-HOOKS-0.1 协议的开放节点集合。
/// </summary>
/// <remarks>
/// <para>
/// 节点集合与协议一致且**不再扩展**：策略控制在输入、模型调用、工具调用与最终输出四类边界上生效。
/// 协议词汇与解析见 <see cref="HookInterceptionPoints"/>。
/// </para>
/// <para>
/// 协议中的 <c>agent_startup</c> / <c>agent_shutdown</c> 表达单次 agent run 边界，与 OneCode 产品会话边界不等价，
/// 故不纳入可执行枚举，仅在 <see cref="HookInterceptionPoints.IsClosedLifecycleWireName"/> 中识别以产出配置诊断。
/// </para>
/// </remarks>
public enum HookInterceptionPoint
{
    /// <summary>外部输入进入 agent run 前。target：输入文本。matcher：无（全量）。</summary>
    [JsonStringEnumMemberName("input")]
    Input,

    /// <summary>模型请求前。target：请求消息。matcher：modelId。</summary>
    [JsonStringEnumMemberName("pre_model_call")]
    PreModelCall,

    /// <summary>模型完整响应后。target：响应内容。matcher：modelId。</summary>
    [JsonStringEnumMemberName("post_model_call")]
    PostModelCall,

    /// <summary>工具执行前。target：工具参数。matcher：工具名（glob）。</summary>
    [JsonStringEnumMemberName("pre_tool_call")]
    PreToolCall,

    /// <summary>工具执行后。target：工具结果。matcher：工具名（glob）。</summary>
    [JsonStringEnumMemberName("post_tool_call")]
    PostToolCall,

    /// <summary>最终响应交付调用方前。target：响应文本。matcher：无（全量）。</summary>
    [JsonStringEnumMemberName("output")]
    Output,
}

/// <summary>
/// 拦截点的协议词汇与开放范围——节点名、matcher 字段、开放性的唯一权威来源。
/// </summary>
public static class HookInterceptionPoints
{
    /// <summary>对用户 DSL 开放的 6 个拦截点，按协议执行顺序排列。</summary>
    public static IReadOnlyList<HookInterceptionPoint> Open { get; } =
    [
        HookInterceptionPoint.Input,
        HookInterceptionPoint.PreModelCall,
        HookInterceptionPoint.PostModelCall,
        HookInterceptionPoint.PreToolCall,
        HookInterceptionPoint.PostToolCall,
        HookInterceptionPoint.Output,
    ];

    /// <summary>全部已实现的拦截点（与 <see cref="Open"/> 同源）。</summary>
    public static IReadOnlyList<HookInterceptionPoint> All => Open;

    /// <summary>是否为 AGENT-HOOKS-0.1 协议中不向用户 DSL 开放的内部生命周期节点名。</summary>
    public static bool IsClosedLifecycleWireName(string? name) =>
        name?.Trim().ToLowerInvariant() is "agent_startup" or "agent_shutdown";

    /// <summary>协议线格式名称（<c>hooks.json</c> 的 <c>event</c> 取值）。</summary>
    public static string ToWireName(HookInterceptionPoint point) => point switch
    {
        HookInterceptionPoint.Input => "input",
        HookInterceptionPoint.PreModelCall => "pre_model_call",
        HookInterceptionPoint.PostModelCall => "post_model_call",
        HookInterceptionPoint.PreToolCall => "pre_tool_call",
        HookInterceptionPoint.PostToolCall => "post_tool_call",
        HookInterceptionPoint.Output => "output",
        _ => point.ToString(),
    };

    /// <summary>按协议线格式名称解析开放拦截点；大小写不敏感。</summary>
    public static bool TryParseWireName(string? name, out HookInterceptionPoint point)
    {
        switch (name?.Trim().ToLowerInvariant())
        {
            case "input":
                point = HookInterceptionPoint.Input;
                return true;
            case "pre_model_call":
                point = HookInterceptionPoint.PreModelCall;
                return true;
            case "post_model_call":
                point = HookInterceptionPoint.PostModelCall;
                return true;
            case "pre_tool_call":
                point = HookInterceptionPoint.PreToolCall;
                return true;
            case "post_tool_call":
                point = HookInterceptionPoint.PostToolCall;
                return true;
            case "output":
                point = HookInterceptionPoint.Output;
                return true;
            default:
                point = default;
                return false;
        }
    }

    /// <summary>
    /// 该拦截点支持的 matcher 字段名；<c>null</c> 表示全量匹配（不支持 matcher）。
    /// </summary>
    public static string? MatcherField(HookInterceptionPoint point) => point switch
    {
        HookInterceptionPoint.PreToolCall or HookInterceptionPoint.PostToolCall => "tool_name",
        HookInterceptionPoint.PreModelCall or HookInterceptionPoint.PostModelCall => "model_id",
        _ => null,
    };

    /// <summary>
    /// 已废弃的 OneCode 专有事件名 → 新拦截点线格式名的映射；不在表中的旧事件无对应节点。
    /// </summary>
    /// <returns>有对应节点时返回其线格式名；无对应节点时返回 <c>null</c>。</returns>
    public static string? TryGetLegacySuccessor(string legacyEventName) => legacyEventName switch
    {
        "PreToolUse" => ToWireName(HookInterceptionPoint.PreToolCall),
        "PostToolUse" => ToWireName(HookInterceptionPoint.PostToolCall),
        "UserPromptSubmit" => ToWireName(HookInterceptionPoint.Input),
        _ => null,
    };
}
