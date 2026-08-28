namespace OneCode.Core.Hooks;

/// <summary>
/// Hook 事件类型。语义与可选 matcher 值的权威声明见 <see cref="HookEventMetadataRegistry"/>。
/// </summary>
public enum HookEvent
{
    /// <summary>工具调用前。matcher：工具名。exit code 2 阻断工具调用。</summary>
    PreToolUse,

    /// <summary>工具调用后。matcher：工具名。可注入附加上下文。</summary>
    PostToolUse,

    /// <summary>
    /// 用户提交 prompt 后触发（会话内每轮提问，纠偏续跑不触发），无 matcher 过滤。
    /// exit code 2 可阻断 prompt 进入运行循环，AdditionalContexts 并入本轮输入。
    /// </summary>
    UserPromptSubmit,

    /// <summary>会话生命周期开始。matcher：来源（resume=继续会话，switch=切换会话，缺省=startup）。</summary>
    SessionStart,

    /// <summary>会话生命周期结束（如程序退出）。matcher：结束原因（如 close）。</summary>
    SessionEnd,

    /// <summary>Stop 事件：agent 主循环终结（无论成功还是异常收场）。matcher：终结原因枚举名。</summary>
    Stop,

    /// <summary>Stop 失败事件：运行因未处理异常中断。matcher：异常类别。</summary>
    StopFailure,

    /// <summary>通知事件（权限审批挂起等）。matcher：通知类型（如 permission_prompt）。</summary>
    Notification,

    /// <summary>压缩前。matcher：manual（手动命令）或 auto（自动压缩）。</summary>
    PreCompact,

    /// <summary>压缩后。matcher：manual 或 auto。ToolResponse 携带摘要，可注入附加上下文。</summary>
    PostCompact,

    /// <summary>Goal 工作流阶段调用。matcher：阶段名（goal-decomposer / goal-sub-decomposer / goal-replanner）。</summary>
    GoalStageInvoke,
}
