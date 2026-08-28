namespace OneCode.Core.Hooks;

/// <summary>
/// SessionEnd 事件的 matcher 值（reason 取值集）。
/// 与 <see cref="HookEventMetadata"/> 中 SessionEnd 条目及 docs/hooks.md 事件表保持同步。
/// </summary>
public static class SessionEndReason
{
    /// <summary>用户显式 /close 关闭会话。</summary>
    public const string Close = "close";

    /// <summary>用户输入退出（/exit、/quit 命令路径）。</summary>
    public const string PromptInputExit = "prompt_input_exit";

    /// <summary>宿主停止 / 进程退出兜底（TUI 退出键、Ctrl+C、DI 容器释放等）。</summary>
    public const string Other = "other";
}
