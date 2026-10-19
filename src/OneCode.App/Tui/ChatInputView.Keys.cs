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
    // 仅单元测试使用：生产代码当前无调用方（测试接缝）。
    internal void DispatchInputKey(Key key) => OnInputKeyPress(_input, key);

    /// <summary>
    /// Simulates the same key bubbling to this view after OnInputKeyPress.
    /// Returns true when the bubble was swallowed (interaction already handled).
    /// </summary>
    // 仅单元测试使用：生产代码当前无调用方（测试接缝）。
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

        // Layer 1: 模态弹层结界（Modal Key Barrier）
        // 若弹层可见但按键仍落入输入框（如无头测试或焦点滞留），一律转交模态结界并吞掉，严禁穿透。
        if (InteractionSession?.IsOverlayVisible == true)
        {
            InteractionSession.DispatchOverlayKey(e);
            _suppressKeyBubble = true;
            e.Handled = true;
            return;
        }

        // —— 交互会话接管（提问向导 / 内联选择器）——
        // 选择题/选择器挂起态：Editor 只读，全部键转发给会话，未消耗的键也
        // 吞掉，防止回落到聊天分支（如 Enter 误提交）。
        // 文本题：Editor 仍需接收可打印字符，只转发导航/取消/确认组合键。
        // 例外：只读检视动作白名单（IsReadOnlyInspectionAction）在挂起态全部放行——
        // 审批工具或计划卡时用户需要翻阅对话/侧栏、调宽侧栏、展开工具入参详情或展开待办详情。
        var interactionSuspended = _interactionSuspended && !_isQuestionMode;
        if (interactionSuspended || IsQuestionInteractionKey(e))
        {
            if (interactionSuspended && IsReadOnlyInspectionAction(earlyAction))
            {
                InteractionSession?.TryExecuteShellAction(earlyAction);
                _suppressKeyBubble = true;
                e.Handled = true;
                return;
            }

            if (InteractionSession?.HandleInteractionKey(e) == true || interactionSuspended)
            {
                _suppressKeyBubble = true;
                e.Handled = true;
                return;
            }
        }

        var action = earlyAction;

        // ── 跨视图与全局动作统一委托给 ReplShell 单一权威入口 ──
        if (InteractionSession?.TryExecuteShellAction(action) == true)
        {
            _suppressKeyBubble = true;
            e.Handled = true;
            return;
        }

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

            // Level 6: If text is not empty, clear it
            if (!string.IsNullOrEmpty(CurrentText))
            {
                ClearInput();
                e.Handled = true;
                return;
            }

            // 空闲且输入框为空：无动作（Esc 链末级 No-op）。
            e.Handled = true;
            return;
        }

        // Esc 在补全激活时经 Resolver 解析为 autocomplete:dismiss
        // （Autocomplete 块声明在 Chat 之后，「后匹配生效」）。
        if (action == KeybindingDefaults.ActionAutocompleteDismiss)
        {
            _completion.Hide();
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

        // Tab（补全激活）——经 Resolver 的 autocomplete:accept；补全未激活时
        // 该动作不命中分发分支，Tab 继续走下方硬编码分支（占位建议 / 斜杠补全 / 循环）。
        if (action == KeybindingDefaults.ActionAutocompleteAccept && _completion.IsCompletionActive)
        {
            HandleTab();
            e.Handled = true;
            return;
        }

        // Tab is hardcoded (not via KeybindingResolver) because it implements
        // context-sensitive behavior that depends on runtime state rather than
        // a single action: accept placeholder suggestion (empty input), open
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
            var lastWord = ExtractLastWord(text);
            if (text.StartsWith('/') || lastWord.Contains('@') || _completion.IsCompletionActive)
            {
                HandleTab();
            }
            else if (!_isBusy)
            {
                // Run 活跃期间不切工作模式：模式决定工具集与权限，运行中切换会让 Plan 工作流
                // 与实际 run 脱节（实测一次误触把 Plan 悬死在 GoalAuto）。Tab 在此仅被吞掉。
                CycleModeRequested?.Invoke();
            }
            e.Handled = true;
            return;
        }

        // 占位建议循环（chat:suggestionPrevious/Next，默认键 Ctrl+←/→，经 Resolver 可重映射）。
        // 占位符不可见时不 Handled：按键落到编辑器做光标移动。
        if (action == KeybindingDefaults.ActionChatSuggestionNext && _placeholderLabel.Visible)
        {
            CycleSuggestionNext();
            e.Handled = true;
            return;
        }
        if (action == KeybindingDefaults.ActionChatSuggestionPrevious && _placeholderLabel.Visible)
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

        // Smart paste: Ctrl+V resolves to chat:paste via KeybindingResolver.
        // Routes to SmartPasteHandler (supports images, file paths, large-text collapsing).
        if (action == KeybindingDefaults.ActionChatPaste)
        {
            OnPasteRequested();
            e.Handled = true;
            return;
        }
    }

    /// <summary>
    /// 审批/选择器挂起态下允许放行的只读检视动作白名单：
    /// 决策前允许翻阅对话区、翻阅或调宽右侧计划栏、切换底部待办面板详情以及展开工具/思考/报错详情块。
    /// </summary>
    internal static bool IsReadOnlyInspectionAction(string? action)
        => action is KeybindingDefaults.ActionChatScrollUp
            or KeybindingDefaults.ActionChatScrollDown
            or KeybindingDefaults.ActionChatPageUp
            or KeybindingDefaults.ActionChatPageDown
            or KeybindingDefaults.ActionSidebarPageUp
            or KeybindingDefaults.ActionSidebarPageDown
            or KeybindingDefaults.ActionSidebarScrollUp
            or KeybindingDefaults.ActionSidebarScrollDown
            or KeybindingDefaults.ActionAppSidebarToggle
            or KeybindingDefaults.ActionAppSidebarWider
            or KeybindingDefaults.ActionAppSidebarNarrower
            or KeybindingDefaults.ActionChatToggleLastDetails;

    private void HandleEnter()
    {
        // 提问模式：优先处理，不受 _isBusy 影响
        if (_isQuestionMode)
        {
            SubmitQuestionAnswer();
            return;
        }

        // busy 时不再阻止提交——OnUserSubmitted 中的队列逻辑会自动入队
        var text = ExpandAttachments(CurrentText).Trim();
        if (string.IsNullOrEmpty(text) && _attachmentRegistry.ImageCount == 0) return;

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
        if (_completion.IsCompletionActive)
        {
            _completion.CycleNext();
            _completionList.SelectedItem = _completion.SelectedIndex;
            return;
        }

        var text = _input.Text?.ToString() ?? string.Empty;
        if (text.StartsWith('/'))
        {
            _completion.UpdateCompletionList(text);
            return;
        }

        if (ExtractLastWord(text).Contains('@'))
        {
            _completion.TryTypeaheadCompletion(text);
            return;
        }

        _completion.Hide();
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
