namespace OneCode.Core.Hooks;

/// <summary>
/// Hook 类型解析器——单一事实源，避免多处重复实现导致默认值不一致。
/// </summary>
public static class HookTypeParser
{
    /// <summary>
    /// 将字符串解析为 HookType。null 返回 Command（省略 type 时的默认行为）。
    /// 未知类型抛出 ArgumentException。
    /// </summary>
    public static HookType Parse(string? type) => type?.ToLowerInvariant() switch
    {
        "command" => HookType.Command,
        "notification" => HookType.Notification,
        "http" => HookType.Http,
        null => HookType.Command,
        _ => throw new ArgumentException($"Unknown hook type: '{type}'. Valid values: command, notification, http."),
    };
}
