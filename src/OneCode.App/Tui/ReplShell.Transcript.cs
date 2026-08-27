using OneCode.Core.Keybindings;

namespace OneCode.App.Tui;

/// <summary>
/// Transcript 对话区导航模式（Ctrl+T 进入 / Esc·i 退出）：
/// 焦点从输入框移到对话流，<see cref="KeybindingContextManager"/> push
/// <see cref="KeybindingDefaults.ContextTranscript"/>；j/k 在可交互行
/// （工具/思考/错误摘要行）之间跳转，Enter 展开/折叠，C 复制当前行。
/// 流式运行或交互会话挂起期间禁止进入（流式禁入）；busy 开始时自动退出，
/// 避免流式插入导致游标漂移。方向键/翻页键由 <see cref="MessageListView"/>
/// 原生滚动先行消费，不经 Resolver。
/// </summary>
public sealed partial class ReplShell
{
    private bool _transcriptNavActive;
    private readonly OneCode.App.Transcript.TranscriptViewModel _transcriptNav;

    /// <summary>导航模式是否激活（状态栏指示与测试断言用）。</summary>
    public bool IsTranscriptNavActive => _transcriptNavActive;

    internal void EnterTranscriptMode()
    {
        if (_transcriptNavActive)
            return;

        // 流式禁入：busy 期间流式预览窗口持续重写尾部行，游标会漂移。
        if (_agentStatusBar.IsBusy)
            return;

        // 交互会话（InlineSelector/QuestionWizard）接管键盘时不进入。
        if (_activeInlineSelector is not null || _activeQuestionWizard is not null)
            return;

        _transcriptNavActive = true;
        _keyContextManager.PushContext(KeybindingDefaults.ContextTranscript);
        _transcript.MessageView.CanFocus = true;
        _transcript.MessageView.SetFocus();
        _agentStatusBar.SetNavigationMode(true);
        SetNeedsDraw();
    }

    internal void ExitTranscriptMode()
    {
        if (!_transcriptNavActive)
            return;

        _transcriptNavActive = false;
        _keyContextManager.PopContext(KeybindingDefaults.ContextTranscript);
        _transcriptNav.Reset();
        _transcript.MessageView.SetNavigationHighlight(-1);
        _transcript.MessageView.CanFocus = false;
        _agentStatusBar.SetNavigationMode(false);
        FocusChatInput();
        SetNeedsDraw();
    }

    /// <summary>
    /// 分发 transcript:* 动作。仅在导航模式激活时消耗；返回 false 放行给
    /// 其余分支（Esc 关闭 overlay 等优先级更高，已在前面处理）。
    /// </summary>
    private bool HandleTranscriptAction(string? action)
    {
        if (!_transcriptNavActive || action is null)
            return false;

        var view = _transcript.MessageView;
        switch (action)
        {
            case KeybindingDefaults.ActionTranscriptExit:
                ExitTranscriptMode();
                return true;

            case KeybindingDefaults.ActionTranscriptNext:
                if (_transcriptNav.Move(+1))
                    view.SetNavigationHighlight(_transcriptNav.CursorLine);
                return true;

            case KeybindingDefaults.ActionTranscriptPrevious:
                if (_transcriptNav.Move(-1))
                    view.SetNavigationHighlight(_transcriptNav.CursorLine);
                return true;

            case KeybindingDefaults.ActionTranscriptTop:
                if (_transcriptNav.MoveToEdge(first: true))
                    view.SetNavigationHighlight(_transcriptNav.CursorLine);
                return true;

            case KeybindingDefaults.ActionTranscriptBottom:
                if (_transcriptNav.MoveToEdge(first: false))
                    view.SetNavigationHighlight(_transcriptNav.CursorLine);
                return true;

            case KeybindingDefaults.ActionTranscriptToggle:
                EnsureTranscriptCursor();
                if (_transcriptNav.CursorLine >= 0)
                {
                    view.ToggleAt(_transcriptNav.CursorLine);
                    // 展开插入详情行导致绝对行号漂移：按序数重取并重定位高亮。
                    _transcriptNav.Refresh();
                    view.SetNavigationHighlight(_transcriptNav.CursorLine);
                }
                return true;

            case KeybindingDefaults.ActionTranscriptCopyCode:
                EnsureTranscriptCursor();
                if (_transcriptNav.CursorLine >= 0)
                    view.TryCopyCodeAt(_transcriptNav.CursorLine);
                return true;

            default:
                return false;
        }
    }

    /// <summary>游标未定位时先落到第一个可交互行（无可交互行则保持 -1）。</summary>
    private void EnsureTranscriptCursor()
    {
        if (_transcriptNav.CursorLine < 0)
            _transcriptNav.MoveToEdge(first: true);
    }
}
