namespace OneCode.Core.Hooks;

/// <summary>
/// 钩子事件元数据——描述每种事件的匹配字段和可用值（用于 UI 展示和文档生成）
/// </summary>
public sealed record HookEventMetadata(
    string Summary,
    string Description,
    MatcherMetadata? MatcherMetadata = null);

/// <summary>
/// 匹配器元数据——定义钩子匹配的字段名和可选值
/// </summary>
public sealed record MatcherMetadata(
    string FieldToMatch,
    IReadOnlyList<string> Values);

/// <summary>
/// 10 种事件的元数据注册表
/// </summary>
public static class HookEventMetadataRegistry
{
    public static readonly IReadOnlyDictionary<HookEvent, HookEventMetadata> All = new Dictionary<HookEvent, HookEventMetadata>
    {
        [HookEvent.PreToolUse] = new(
            "工具执行前",
            "在工具调用执行前触发，可通过 exit code 2 阻止工具执行",
            new MatcherMetadata("tool_name", ["Bash", "Write", "Read", "Grep", "Glob", "WebFetch", "WebSearch", "Task"])),
        [HookEvent.PostToolUse] = new(
            "工具执行后",
            "在工具调用成功执行后触发",
            new MatcherMetadata("tool_name", ["Bash", "Write", "Read", "Grep", "Glob", "WebFetch", "WebSearch", "Task"])),
        [HookEvent.Notification] = new(
            "通知发送时",
            "在发送通知时触发",
            new MatcherMetadata("notification_type", ["permission_prompt", "idle_prompt", "auth_success"])),
        [HookEvent.UserPromptSubmit] = new(
            "用户提交提示",
            "在用户提交 prompt 后触发，无 matcher 过滤"),
        [HookEvent.SessionStart] = new(
            "会话启动",
            "在新会话启动时触发",
            new MatcherMetadata("source", ["startup", "resume", "clear", "compact"])),
        [HookEvent.Stop] = new(
            "停止前",
            "在 AI 响应结束前触发，可通过 exit code 2 阻止停止"),
        [HookEvent.StopFailure] = new(
            "停止失败",
            "在 API 错误导致 turn 结束时触发",
            new MatcherMetadata("error", ["rate_limit", "auth_failed", "billing", "invalid_request", "server_error", "max_output_tokens", "unknown"])),
        [HookEvent.PreCompact] = new(
            "压缩前",
            "在对话压缩前触发",
            new MatcherMetadata("trigger", ["manual", "auto"])),
        [HookEvent.PostCompact] = new(
            "压缩后",
            "在对话压缩后触发",
            new MatcherMetadata("trigger", ["manual", "auto"])),
        [HookEvent.SessionEnd] = new(
            "会话结束",
            "在会话结束时触发",
            new MatcherMetadata("reason", ["clear", "logout", "prompt_input_exit", "other"])),
    };
}
