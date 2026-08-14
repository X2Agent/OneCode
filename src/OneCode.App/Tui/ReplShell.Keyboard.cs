using OneCode.Core.Keybindings;

namespace OneCode.App.Tui;

/// <summary>
/// Keyboard dispatch and inline selector management for <see cref="ReplShell"/>.
/// Extracted as a partial to keep the main file under the 300-line guideline.
///
/// Hosts the OnKeyDown override (mode selector, review overlay, LSP diagnostics
/// overlay, plan card interaction) and the InlineSelector lifecycle
/// (show/dismiss/refresh).
/// </summary>
public sealed partial class ReplShell
{
    // Keyboard: design-spec §5
    protected override bool OnKeyDown(Key kb)
    {
        // Question wizard key handling takes highest priority when active
        if (_activeQuestionWizard is not null)
        {
            // 长文本模式特殊处理：将输入转发到 ChatInputView
            if (_activeQuestionWizard.IsLongTextMode && _chatInput.HasFocus)
            {
                // Ctrl+Enter 提交长文本
                if (kb == Key.Enter.WithCtrl)
                {
                    var answer = _chatInput.CurrentText.Trim();
                    _activeQuestionWizard.SetTextAnswer(answer);
                    _chatInput.ClearQuestionMode();
                    _activeQuestionWizard.HandleKey(kb); // 触发下一题或完成
                    RefreshQuestionWizard();
                    return true;
                }

                // Ctrl+Shift+Enter 上一题
                if (kb == Key.Enter.WithCtrl.WithShift)
                {
                    var answer = _chatInput.CurrentText.Trim();
                    _activeQuestionWizard.SetTextAnswer(answer);
                    _chatInput.ClearQuestionMode();
                    _activeQuestionWizard.HandleKey(kb);
                    RefreshQuestionWizard();
                    // 重新进入长文本模式
                    EnterLongTextModeIfNeeded();
                    return true;
                }

                // Esc 取消向导
                if (kb == Key.Esc)
                {
                    _activeQuestionWizard.CancelWizard();
                    _chatInput.ClearQuestionMode();
                    return true;
                }

                // 其他键不消耗，让 ChatInputView 处理（用于文本输入）
                return false;
            }

            if (kb == Key.Esc)
            {
                _activeQuestionWizard.CancelWizard();
                return true;
            }

            var consumed = _activeQuestionWizard.HandleKey(kb);
            if (consumed)
            {
                RefreshQuestionWizard();
                // 如果是短文本题，进入输入模式
                if (_activeQuestionWizard.CurrentQuestion.Type == QuestionType.ShortText)
                {
                    EnterShortTextMode();
                }
                return true;
            }
        }

        // Inline selector key handling takes priority when active
        if (_activeInlineSelector is not null)
        {
            var consumed = _activeInlineSelector.HandleKey(kb);
            if (consumed)
            {
                RefreshInlineSelector();
                return true;
            }
        }

        // Completion popup ESC handling — the completion popup is NOT managed by
        // OverlayHost (it's added/removed via OnCompletionStateChanged), so it
        // needs its own ESC handler here. This catches ESC even when the Editor
        // consumes it internally (e.g., for canceling selection) before it reaches
        // ChatInputView.OnInputKeyPress.
        if (kb == Key.Esc && _completionVisible)
        {
            _chatInput.HideCompletion();
            FocusChatInput();
            return true;
        }

        // Overlay Esc handling — safety net for when neither the overlay itself
        // nor OverlayHost.OnKeyDown consumed the key (e.g., focus anomaly).
        // Under normal conditions OverlayHost.OnKeyDown handles ESC first.
        if (kb == Key.Esc && _overlayHost.IsOverlayVisible)
        {
            _overlayHost.HandleEsc();
            _overlayHost.Visible = _overlayHost.IsOverlayVisible;
            return true;
        }

        // KeybindingResolver — all configurable shortcuts go through here.
        // ESC for overlays/completion is handled above (not configurable) because
        // dismissal must always work regardless of keybinding overrides.
        var action = TryResolveKey(kb);

        // Ctrl+D — exit the application (when prompt is not focused).
        // When the prompt has focus, ChatInputView.OnInputKeyPress handles it instead.
        if (action == KeybindingDefaults.ActionAppExit && !_chatInput.HasFocus)
        {
            _app.RequestStop();
            return true;
        }

        return base.OnKeyDown(kb);
    }

    /// <summary>
    /// Resolves a key press to an action string via KeybindingResolver.
    /// Returns null when no binding matches — callers must not fall back to
    /// hardcoded keys; unmatched keys are simply not handled.
    /// </summary>
    private string? TryResolveKey(Key key)
    {
        var adapter = new TuiKeyAdapter(key);
        return adapter.ResolveAction(_keyResolver, _keyContextManager.ActiveContexts);
    }

}
