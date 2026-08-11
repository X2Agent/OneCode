using System.Text.RegularExpressions;

namespace OneCode.App.Services.Hooks;

/// <summary>
/// 渲染 Hook 配置中的 <c>{{Field}}</c> 模板字符串。
///
/// 支持的字段（与 <see cref="OneCode.Core.Hooks.HookPayload"/> 字段一一对应）：
/// <list type="bullet">
///   <item><c>Event</c> — Hook 事件类型字符串</item>
///   <item><c>SessionId</c> — 会话 ID</item>
///   <item><c>Cwd</c> — 当前工作目录</item>
///   <item><c>ToolName</c> — 触发 Hook 的工具名</item>
///   <item><c>UserMessage</c> — 用户消息内容</item>
///   <item><c>AgentId</c> — Agent ID</item>
///   <item><c>AgentType</c> — Agent 类型</item>
///   <item><c>Timestamp</c> — 时间戳（yyyy-MM-dd HH:mm:ss）</item>
/// </list>
///
/// 未知字段保持原样不替换。
/// </summary>
internal static partial class HookTemplateRenderer
{
    /// <summary>
    /// 替换模板中的 <c>{{Field}}</c> 占位符为 <see cref="HookPayload"/> 实际字段值。
    /// </summary>
    public static string Render(string template, HookPayload payload)
    {
        if (string.IsNullOrEmpty(template))
            return template;

        return TemplatePattern().Replace(template, match =>
        {
            var field = match.Groups[1].Value;
            return field switch
            {
                "Event" => payload.Event.ToString(),
                "SessionId" => payload.SessionId ?? string.Empty,
                "Cwd" => payload.Cwd ?? string.Empty,
                "ToolName" => payload.ToolName ?? string.Empty,
                "UserMessage" => payload.UserMessage ?? string.Empty,
                "AgentId" => payload.AgentId ?? string.Empty,
                "AgentType" => payload.AgentType ?? string.Empty,
                "Timestamp" => payload.Timestamp.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                _ => match.Value, // 未知字段保持原样
            };
        });
    }

    [GeneratedRegex(@"\{\{(\w+)\}\}")]
    private static partial Regex TemplatePattern();
}
