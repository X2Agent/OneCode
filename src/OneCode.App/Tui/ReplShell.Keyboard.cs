using OneCode.Core.Keybindings;

namespace OneCode.App.Tui;

/// <summary>
/// <see cref="ReplShell"/> 的键盘路由与跨区域动作唯一权威执行入口。
/// 输入框聚焦时由 <see cref="ChatInputView"/> 经 <see cref="IInteractionSession"/> 委托至此；
/// 焦点不在输入框时由 <see cref="OnKeyDown"/> 按五层互斥优先级兜底分发。
/// </summary>
public sealed partial class ReplShell
{
    internal bool DispatchKeyDown(Key kb) => OnKeyDown(kb);

    internal bool IsGlobalExitKey(Key kb) =>
        TuiKeyAdapter.ResolveAction(kb, _keyResolver, _keyContextManager.ActiveContexts)
        == KeybindingDefaults.ActionAppExit;

    public bool IsOverlayVisible => _overlayHost.IsOverlayVisible;

    public bool DispatchOverlayKey(Key kb)
    {
        if (!_overlayHost.IsOverlayVisible)
            return false;

        if (kb == Key.Esc)
        {
            _overlayHost.HandleEsc();
            _overlayHost.Visible = _overlayHost.IsOverlayVisible;
            return true;
        }

        if (_overlayHost.Top is { } top)
            _ = top.NewKeyDownEvent(kb);
        return true;
    }

    /// <summary>
    /// 跨视图与全局动作的唯一权威执行路径（DESIGN.md 规范：同一个动作只能有一个权威执行路径）。
    /// </summary>
    public bool TryExecuteShellAction(string? action)
    {
        switch (action)
        {
            case KeybindingDefaults.ActionChatScrollUp:
                _transcript.MessageView.ScrollUp();
                return true;
            case KeybindingDefaults.ActionChatScrollDown:
                _transcript.MessageView.ScrollDown();
                return true;
            case KeybindingDefaults.ActionChatPageUp:
                _transcript.MessageView.PageUp();
                return true;
            case KeybindingDefaults.ActionChatPageDown:
                _transcript.MessageView.PageDown();
                return true;
            case KeybindingDefaults.ActionSidebarPageUp:
                ActiveSidebar?.PageUp();
                return true;
            case KeybindingDefaults.ActionSidebarPageDown:
                ActiveSidebar?.PageDown();
                return true;
            case KeybindingDefaults.ActionSidebarScrollUp:
                ActiveSidebar?.ScrollUp();
                return true;
            case KeybindingDefaults.ActionSidebarScrollDown:
                ActiveSidebar?.ScrollDown();
                return true;
            case KeybindingDefaults.ActionAppSidebarToggle:
                ToggleSidebarVisibility();
                return true;
            case KeybindingDefaults.ActionAppSidebarWider:
                AdjustSidebarWidth(SidebarViewBase.KeyboardResizeStep);
                return true;
            case KeybindingDefaults.ActionAppSidebarNarrower:
                AdjustSidebarWidth(-SidebarViewBase.KeyboardResizeStep);
                return true;
            case KeybindingDefaults.ActionChatToggleLastDetails:
                _transcript.ToggleLastDetails();
                return true;
            case KeybindingDefaults.ActionChatFind:
                _chatInput.BeginSearchMode();
                return true;
            case KeybindingDefaults.ActionChatFindNext:
                if (_chatInput.IsSearchMode)
                {
                    var (nextTotal, nextIdx) = _transcript.FindNext();
                    _chatInput.SetSearchMatchStatus(nextTotal, nextIdx);
                }
                return true;
            case KeybindingDefaults.ActionChatFindPrevious:
                if (_chatInput.IsSearchMode)
                {
                    var (prevTotal, prevIdx) = _transcript.FindPrevious();
                    _chatInput.SetSearchMatchStatus(prevTotal, prevIdx);
                }
                return true;
            case KeybindingDefaults.ActionChatFindDismiss:
                _chatInput.EndSearchMode();
                return true;
            default:
                return false;
        }
    }

    protected override bool OnKeyDown(Key kb)
    {
        var action = TuiKeyAdapter.ResolveAction(kb, _keyResolver, _keyContextManager.ActiveContexts);

        // Layer 0: Ctrl+D (app:exit) 始终最高优先级放行（输入框未聚焦时由此兜底退出）
        if (action == KeybindingDefaults.ActionAppExit && !_chatInput.HasFocus)
        {
            _app.RequestStop();
            return true;
        }

        // Layer 1: 模态弹层结界（Modal Key Barrier）
        // 弹层可见时，Esc 关闭顶层弹层，其余任何未被弹层消费的冒泡按键一律在此截停，
        // 严禁穿透触发底层搜索、面板切换或滚动，防止焦点被抢回弹层背后。
        if (_overlayHost.IsOverlayVisible)
        {
            return DispatchOverlayKey(kb);
        }

        // Layer 2: 自动补全浮窗 (Autocomplete)
        if (_completionVisible && kb == Key.Esc)
        {
            _chatInput.HideCompletion();
            FocusChatInput();
            return true;
        }

        // Layer 3: 内联交互会话 (Inline Selector / Question Wizard)
        if (HasActiveInteractionSession())
        {
            if (kb == Key.Esc)
            {
                DismissActiveSession();
                return true;
            }

            if (!_chatInput.HasInputFocus)
            {
                if (!_chatInput.IsQuestionMode && ChatInputView.IsReadOnlyInspectionAction(action))
                {
                    TryExecuteShellAction(action);
                    return true;
                }

                if (HandleInteractionKey(kb) || !_chatInput.IsQuestionMode)
                    return true;
            }
        }

        // Layer 4 & 5: 搜索模式与常规跨视图动作（焦点不在输入框时由此兜底；
        // 焦点在输入框时已由 ChatInputView.OnInputKeyPress 委托 TryExecuteShellAction 处理）。
        if (!_chatInput.HasInputFocus && TryExecuteShellAction(action))
        {
            return true;
        }

        return base.OnKeyDown(kb);
    }
}
