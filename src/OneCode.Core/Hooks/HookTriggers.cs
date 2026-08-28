namespace OneCode.Core.Hooks;

/// <summary>
/// 压缩类 hook（PreCompact/PostCompact）的触发方式常量，同时作为 matcher 值。
/// </summary>
public static class HookTriggers
{
    /// <summary>手动触发（/compact 命令）。</summary>
    public const string Manual = "manual";

    /// <summary>自动触发（AutoCompactService / PromptTooLong 恢复链）。</summary>
    public const string Auto = "auto";
}
