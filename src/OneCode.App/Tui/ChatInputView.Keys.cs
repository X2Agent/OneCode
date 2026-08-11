using OneCode.Core.Keybindings;

namespace OneCode.App.Tui;

/// <summary>
/// Keyboard dispatch for <see cref="ChatInputView"/>.
/// Extracted as a partial to keep the main view file under the 300-line guideline.
/// Hosts the 195-line <see cref="OnInputKeyPress"/> switch and its private helpers.
/// </summary>
public sealed partial class ChatInputView
{
    private void OnInputKeyPress(object? sender, Key e)
    {
        // Ctrl+D (app:exit) must work even when interaction is suspended
        // (InlineSelector/QuestionWizard active) — otherwise the user is
        // trapped and cannot exit without dismissing the selector first.
        var earlyAction = TryResolveKey(e);
        if (earlyAction == KeybindingDefaults.ActionAppExit)
        {
            QuitRequested?.Invoke();
            e.Handled = true;
            return;
        }

        // Ctrl+Enter is not a configurable chat binding, but long-text wizard
        // questions reserve it for submit (Ctrl+Shift+Enter goes to previous).
        if (_isQuestionMode && _isLongTextMode && e == Key.Enter.WithCtrl)
        {
            QuestionLongTextNavigationRequested?.Invoke(false);
            e.Handled = true;
            return;
        }
        if (_isQuestionMode && _isLongTextMode && e == Key.Enter.WithCtrl.WithShift)
        {
            QuestionLongTextNavigationRequested?.Invoke(true);
            e.Handled = true;
            return;
        }

        // Text questions preserve bare Left/Right for caret movement. Alt+Left/Right
        // is reserved for switching wizard questions and must be handled before the
        // Editor's normal text-navigation path.
        if (_isQuestionMode && e.IsAlt && !e.IsCtrl && !e.IsShift)
        {
            var bare = e.NoShift.NoCtrl.NoAlt;
            if (bare == Key.CursorLeft || bare == Key.CursorRight)
            {
                QuestionTextNavigationRequested?.Invoke(bare == Key.CursorLeft);
                e.Handled = true;
                return;
            }
        }

        // Question text input owns the Editor even while a wizard is active. It must
        // run before the generic suspended-input forwarding path; otherwise Enter,
        // Esc and all typed characters are swallowed by the wizard suspension state.
        if (_isQuestionMode)
        {
            if (e.NoShift.NoCtrl.NoAlt == Key.Esc)
            {
                QuestionCancelRequested?.Invoke();
                e.Handled = true;
                return;
            }

            if (_isLongTextMode && e.NoShift.NoCtrl.NoAlt == Key.Enter && e.IsCtrl)
            {
                QuestionLongTextNavigationRequested?.Invoke(e.IsShift);
                e.Handled = true;
                return;
            }

            if (!_isLongTextMode && e.NoShift.NoCtrl.NoAlt == Key.Enter && e.IsShift)
            {
                QuestionPreviousRequested?.Invoke();
                e.Handled = true;
                return;
            }
        }

        if (_interactionSuspended && !_isQuestionMode)
        {
            // Editor has focus and consumes all keys; ReplShell.OnKeyDown never fires.
            // Forward the key so ReplShell can route it to the active InlineSelector
            // or choice-based QuestionWizard.
            InteractionSuspendedKeyForwarded?.Invoke(e);
            e.Handled = true;
            return;
        }

        // Reuse the already-resolved action for the rest of the switch
        var action = earlyAction;

        // Newline MUST be checked before plain Enter.
        // Short-text wizard questions reserve Shift+Enter for "previous question".
        // Long-text questions accept ordinary/newline bindings for document input.
        if (action == KeybindingDefaults.ActionChatNewline)
        {
            if (_isQuestionMode && !_isLongTextMode)
            {
                QuestionPreviousRequested?.Invoke();
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

    public void AcceptCompletion()
    {
        var accepted = _completion.Accept();
        if (accepted is not null)
        {
            _suppressCompletion = true;
            _input.Text = accepted;
            _input.InsertionPoint = _input.Text.Length;
            _suppressCompletion = false;
        }
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
