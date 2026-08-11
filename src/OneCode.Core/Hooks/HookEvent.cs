namespace OneCode.Core.Hooks;

/// <summary>
/// 钩子事件类型——收敛到 10 种真实有外部脚本消费价值的事件
///
/// 钩子系统是 OneCode 的核心扩展机制，支持 CI/CD 集成、安全策略、自动化工作流。
/// 每种事件在特定生命周期点触发，钩子可以通过 stdout 返回 JSON 控制行为。
///
/// 退出码约定：
/// - 0: 成功（stdout 内容因事件而异）
/// - 2: 阻断（stderr 显示给模型或用户，具体行为因事件而异）
/// - 其他: stderr 仅显示给用户
///
/// </summary>
public enum HookEvent
{
    PreToolUse,
    PostToolUse,
    Notification,
    UserPromptSubmit,
    SessionStart,
    Stop,
    StopFailure,
    PreCompact,
    PostCompact,
    SessionEnd,
}
