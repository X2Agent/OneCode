namespace OneCode.App.Tui;

/// <summary>
/// ReplShell 侧活跃交互会话（提问向导 / 内联选择器 / 计划卡审批面板）。
/// ChatInputView 在交互接管期间把按键交给会话统一处理，替代此前的松散
/// 转发事件（InteractionSuspendedKeyForwarded / Question*Requested 系列），
/// 让「键 → 交互域动作」的翻译只存在一份（D1）。
/// </summary>
internal interface IInteractionSession
{
    /// <summary>
    /// 处理交互期间的按键。选择题/选择器挂起态转发全部键；文本题仅转发
    /// 导航/取消/确认组合键，其余键仍由 Editor 处理。
    /// 返回 true 表示按键已消耗。
    /// </summary>
    bool HandleInteractionKey(Key key);

    /// <summary>
    /// 提问模式下 chat:newline 映射触发（短文本题回到上一题，
    /// 与原生 Shift+Enter 行为一致）。
    /// </summary>
    void HandleQuestionNewline();
}
