namespace OneCode.Core.Hooks;

/// <summary>
/// 会话关闭原因（产品状态值，仅供日志与宿主收尾使用）。
/// SessionEnd 已不再是 Hook 拦截点（见 <see cref="HookInterceptionPoints"/>），
/// 本类型不再作为 matcher 值消费。
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
