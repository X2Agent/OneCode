namespace OneCode.Core.Hooks;

/// <summary>
/// 钩子注册项
/// </summary>
public sealed record HookRegistration
{
    /// <summary>Hook 名称（用于调试和移除）</summary>
    public string Name { get; init; } = string.Empty;

    public HookEvent Event { get; init; }

    /// <summary>匹配器 pattern："" 或 "*" 匹配所有</summary>
    public string Matcher { get; init; } = string.Empty;

    /// <summary>
    /// 执行优先级（越小越先执行）：
    ///   0-99:   系统内置 hook
    ///   100-199: 用户 settings.json hook
    ///   200-299: 项目 settings.json hook
    /// </summary>
    public int Priority { get; init; } = 100;

    /// <summary>是否只执行一次，执行后自动移除</summary>
    public bool Once { get; init; }

    public HookType ExecutorType { get; init; } = HookType.Command;

    /// <summary>超时时间（毫秒）</summary>
    public int TimeoutMs { get; init; } = 5000;

    /// <summary>类型专属配置（来自 settings.json）</summary>
    public HookConfig? Config { get; init; }
}
