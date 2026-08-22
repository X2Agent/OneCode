using OneCode.Core.Keybindings;

namespace OneCode.App.Tui;

/// <summary>
/// Keyboard dispatch for <see cref="ChatInputView"/> — the single key-resolution
/// point of the TUI. Hosts <see cref="OnInputKeyPress"/>: KeybindingResolver
/// lookup, interaction-session routing while suspended/question mode, and the
/// chat-branch switch (completion / history / submit / global shortcuts).
/// Kept as a partial because key handling is Editor-framework-bound and shares
/// the view's private state.
/// </summary>
public sealed partial class ChatInputView
{
    /// <summary>
    /// Dispatches a key as if the nested editor raised KeyDownEvent.
    /// Tests use this to exercise the OnInputKeyPress path without a live driver loop.
    /// </summary>
    internal void DispatchInputKey(Key key) => OnInputKeyPress(_input, key);

    /// <summary>
    /// Simulates the same key bubbling to this view after OnInputKeyPress.
    /// Returns true when the bubble was swallowed (interaction already handled).
    /// </summary>
    internal bool DispatchBubbledKeyDown(Key key) => OnKeyDown(key);

    protected override bool OnKeyDown(Key key)
    {
        // Terminal.Gui may drop Key.Handled (value-type Key). Swallow the bubble
        // here so ReplShell.OnKeyDown cannot re-run HandleInteractionKey.
        if (_suppressKeyBubble)
        {
            _suppressKeyBubble = false;
            return true;
        }

        return base.OnKeyDown(key);
    }

    private void OnInputKeyPress(object? sender, Key e)
    {
        _suppressKeyBubble = false;

        // Ctrl+D (app:exit) must work even when interaction is suspended
        // (InlineSelector/QuestionWizard active) — otherwise the user is
        // trapped and cannot exit without dismissing the selector first.
        var earlyAction = TuiKeyAdapter.ResolveAction(e, _keyResolver, _keyContextManager.ActiveContexts);
        if (earlyAction == KeybindingDefaults.ActionAppExit)
        {
            QuitRequested?.Invoke();
            e.Handled = true;
            return;
        }

        // —— 交互会话接管（提问向导 / 内联选择器）——
        // 选择题/选择器挂起态：Editor 只读，全部键转发给会话，未消耗的键也
        // 吞掉，防止回落到聊天分支（如 Enter 误提交）。
        // 文本题：Editor 仍需接收可打印字符，只转发导航/取消/确认组合键。
        var interactionSuspended = _interactionSuspended && !_isQuestionMode;
        if (interactionSuspended || IsQuestionInteractionKey(e))
        {
            if (InteractionSession?.HandleInteractionKey(e) == true || interactionSuspended)
            {
                _suppressKeyBubble = true;
                e.Handled = true;
                return;
            }
        }

        // Reuse the already-resolved action for the rest of the switch
        var action = earlyAction;

        // Newline MUST be checked before plain Enter.
        // 短文本提问模式下 newline 映射 = 回到上一题；其余情况插入换行。
        if (action == KeybindingDefaults.ActionChatNewline)
        {
            if (_isQuestionMode && !_isLongTextMode)
            {
                InteractionSession?.HandleQuestionNewline();
            }
            else
            {
                AddNewline();
            }
            e.Handled = true;
            return;
        }

        // Enter — submit or accept completion
        if (action == KeybindingDefaults.ActionChatSubmit)
        {
            if (_completion.IsCompletionActive)
            {
                AcceptCompletion();
                e.Handled = true;
                return;
            }
            HandleEnter();
            e.Handled = true;
            return;
        }

        // Escape — dismiss completion or stop model response
        if (action == KeybindingDefaults.ActionChatCancel)
        {
            if (_completion.IsCompletionActive)
            {
                _completion.Hide();
                e.Handled = true;
                return;
            }

            if (_isBusy)
            {
                InterruptRequested?.Invoke();
                e.Handled = true;
                return;
            }

            // Idle: do not clear input text (user can Ctrl+A + Delete).
            e.Handled = true;
            return;
        }

        // Custom / chord binding for interrupt (e.g. ctrl+x ctrl+k → chat:killAgents).
        // Same behavior as Esc while busy: cancel the running query without exiting.
        if (action == KeybindingDefaults.ActionChatKillAgents)
        {
            if (_isBusy)
                InterruptRequested?.Invoke();
            e.Handled = true;
            return;
        }

        // Tab is hardcoded (not via KeybindingResolver) because it implements
        // context-sensitive behavior that depends on runtime state rather than
        // a single action: accept placeholder suggestion (empty input), cycle
        // slash-command completion (/prefix), or cycle working mode (default).
        // Each behavior is a distinct action, making a single binding impractical.
        if (e == Key.Tab)
        {
            var text = _input.Text?.ToString() ?? string.Empty;
            if (string.IsNullOrEmpty(text) && _suggestions.Count > 0 && _placeholderLabel.Visible)
            {
                AcceptSuggestion();
                e.Handled = true;
                return;
            }
            if (text.StartsWith('/') || _completion.IsCompletionActive)
            {
                HandleTab();
            }
            else
            {
                CycleModeRequested?.Invoke();
            }
            e.Handled = true;
            return;
        }

        // Shift+Tab — 在 TEAM 模式下切换 Magentic ↔ GroupChat 策略。
        // 在补全激活时，Shift+Tab 仍由 ContextAutocomplete 的 shift+tab=confirm:cycleMode 处理，
        // 但补全未激活时由这里拦截，调用 ToggleStrategyRequested。
        if (action == KeybindingDefaults.ActionChatToggleStrategy && !_completion.IsCompletionActive)
        {
            ToggleStrategyRequested?.Invoke();
            e.Handled = true;
            return;
        }

        // Ctrl+Shift+T — 在 TEAM 模式下循环切换已注册团队。
        // 补全激活时不拦截（避免与可能的补全键冲突）。
        if (action == KeybindingDefaults.ActionChatCycleTeam && !_completion.IsCompletionActive)
        {
            CycleTeamRequested?.Invoke();
            e.Handled = true;
            return;
        }

        // Ctrl+G — 切换右侧计划侧边栏（有活动计划时可收起/展开）。
        if (action == KeybindingDefaults.ActionChatTogglePlanPanel && !_completion.IsCompletionActive)
        {
            TogglePlanPanelRequested?.Invoke();
            e.Handled = true;
            return;
        }

        // Ctrl+Right / Ctrl+Left — cycle through suggestions when placeholder is visible.
        if (e == Key.CursorRight.WithCtrl && _placeholderLabel.Visible)
        {
            CycleSuggestionNext();
            e.Handled = true;
            return;
        }
        if (e == Key.CursorLeft.WithCtrl && _placeholderLabel.Visible)
        {
            CycleSuggestionPrevious();
            e.Handled = true;
            return;
        }

        // Up/Down — completion navigation or history navigation (via KeybindingResolver)
        // ContextAutocomplete 由 ChatInputView 在补全状态变化时 push/pop，因此：
        //   补全激活时 → action 解析为 autocomplete:previous/next
        //   补全未激活时 → action 解析为 history:previous/next（ContextChat 注册）
        // 用户可通过 keybindings.json 重映射这些键，不再有硬编码 fallback。
        if (action == KeybindingDefaults.ActionAutocompletePrevious)
        {
            _completion.CyclePrevious();
            _completionList.SelectedItem = _completion.SelectedIndex;
            e.Handled = true;
            return;
        }

        if (action == KeybindingDefaults.ActionHistoryPrevious)
        {
            if (_input.CurrentRow == 0)
            {
                HandleHistoryUp();
                e.Handled = true;
            }
            // 光标不在第一行时不 Handled — 让 Editor 处理光标上移
            return;
        }

        // Ctrl+Up — 无条件召回最后一条消息（编辑重发）
        if (action == KeybindingDefaults.ActionHistoryRecallLast)
        {
            var text = _historyCtrl.RecallLast();
            if (text is not null) SetInputText(text);
            e.Handled = true;
            return;
        }

        if (action == KeybindingDefaults.ActionAutocompleteNext)
        {
            _completion.CycleNext();
            _completionList.SelectedItem = _completion.SelectedIndex;
            e.Handled = true;
            return;
        }

        if (action == KeybindingDefaults.ActionHistoryNext)
        {
            if (_input.CurrentRow >= _input.LineCount - 1)
            {
                HandleHistoryDown();
                e.Handled = true;
            }
            // 光标不在最后一行时不 Handled — 让 Editor 处理光标下移
            return;
        }

        // Ctrl+D — exit the application.
        if (action == KeybindingDefaults.ActionAppExit)
        {
            QuitRequested?.Invoke();
            e.Handled = true;
            return;
        }

        // Shift+Up/Down or Ctrl+PgUp/PgDn — scroll conversation transcript (line-level)
        if (action == KeybindingDefaults.ActionChatScrollUp)
        {
            ScrollUpRequested?.Invoke();
            e.Handled = true;
            return;
        }
        if (action == KeybindingDefaults.ActionChatScrollDown)
        {
            ScrollDownRequested?.Invoke();
            e.Handled = true;
            return;
        }

        // PageUp / PageDown — scroll the conversation transcript (page-level)
        if (action == KeybindingDefaults.ActionChatPageUp)
        {
            PageUpRequested?.Invoke();
            e.Handled = true;
            return;
        }
        if (action == KeybindingDefaults.ActionChatPageDown)
        {
            PageDownRequested?.Invoke();
            e.Handled = true;
            return;
        }

        // Smart paste: Ctrl+V resolves to chat:paste via KeybindingResolver.
        // Routes to SmartPasteHandler (supports images, file paths, large-text collapsing).
        if (action == KeybindingDefaults.ActionChatPaste)
        {
            OnPasteRequested();
            e.Handled = true;
            return;
        }
    }

    private void HandleEnter()
    {
        // 提问模式：优先处理，不受 _isBusy 影响
        if (_isQuestionMode)
        {
            SubmitQuestionAnswer();
            return;
        }

        // busy 时不再阻止提交——OnUserSubmitted 中的队列逻辑会自动入队
        var text = (_pastedText ?? CurrentText).Trim();
        if (string.IsNullOrEmpty(text) && _pendingImages.Count == 0) return;

        if (text is "/quit" or "/q" or "/exit" or "exit" or "quit")
        {
            QuitRequested?.Invoke();
            return;
        }

        _historyCtrl.ResetNavigation();
        ClearInput();
        Submitted?.Invoke(text);
    }

    private void HandleHistoryUp()
    {
        var text = _historyCtrl.NavigateUp(CurrentText);
        if (text is not null) SetInputText(text);
    }

    private void HandleHistoryDown()
    {
        var text = _historyCtrl.NavigateDown();
        if (text is not null) SetInputText(text);
    }

    private void HandleTab()
    {
        var text = _input.Text?.ToString() ?? string.Empty;
        if (!text.StartsWith('/'))
        {
            _completion.Hide();
            return;
        }

        if (!_completion.IsCompletionActive)
        {
            _completion.UpdateCompletionList(text);
            return;
        }

        _completion.CycleNext();
        _completionList.SelectedItem = _completion.SelectedIndex;
    }

    /// <summary>
    /// 提问模式下需要交给交互会话的组合键，其余键仍由 Editor / 聊天分支处理。
    /// 通用：Esc；长文本：Ctrl+Enter / Ctrl+Shift+Enter；短文本：Shift+Enter、Alt+←/→。
    /// </summary>
    private bool IsQuestionInteractionKey(Key e)
    {
        if (!_isQuestionMode) return false;
        var bare = e.NoShift.NoCtrl.NoAlt;
        if (bare == Key.Esc) return true;
        if (_isLongTextMode)
            return e == Key.Enter.WithCtrl || e == Key.Enter.WithCtrl.WithShift;
        if (e.IsAlt && !e.IsCtrl && !e.IsShift && (bare == Key.CursorLeft || bare == Key.CursorRight))
            return true;
        return bare == Key.Enter && e.IsShift;
    }
}
