namespace OneCode.App.Tui;

/// <summary>
/// InlineSelector and QuestionWizard lifecycle management extracted from ReplShell.
/// Manages the show/dismiss/refresh cycle for inline selector overlays (permission prompts)
/// and multi-question interactive wizards within the conversation view.
///
/// This partial also implements <see cref="IInteractionSession"/>: interaction keys are
/// routed here from ChatInputView (and the ReplShell.OnKeyDown fallback), replacing the
/// former loose forwarding events and the triplicated wizard/selector dispatch logic.
/// Interaction lines live in a MessageListView tail region — line counts are tracked by
/// the view, not by hand-rolled bookkeeping fields.
/// </summary>
public sealed partial class ReplShell : IInteractionSession
{
    // At most one interaction session is active (no stack). Plan-card approval
    // reuses InlineSelector, so these two pointers are the whole session set.
    private InlineSelector? _activeInlineSelector;
    private QuestionWizard? _activeQuestionWizard;

    public void ShowInlineSelector(InlineSelector selector)
    {
        DismissActiveSession();
        _activeInlineSelector = selector;
        _chatInput.SetInteractionSuspended(true);

        _transcript.MessageView.BeginTailRegion(InlineSelector.RenderAsLines(
            selector.Title,
            selector.Options,
            selector.SelectedIndex,
            selector.Prompt,
            selector.UseInformationRequestCard));
    }

    public void DismissInlineSelector()
    {
        if (_activeInlineSelector is null) return;

        // Complete waiters fail-closed so a replaced/torn-down selector cannot hang.
        _activeInlineSelector.Dismiss();
        _activeInlineSelector = null;
        _transcript.MessageView.EndTailRegion();
        _chatInput.SetInteractionSuspended(false);
        FocusChatInput();
    }

    /// <summary>
    /// 显示多问题向导，接管键盘输入直到向导完成或取消。
    /// </summary>
    public void ShowQuestionWizard(QuestionWizard wizard)
    {
        DismissActiveSession();
        _activeQuestionWizard = wizard;
        UpdateQuestionWizardInputState();

        _transcript.MessageView.BeginTailRegion(wizard.RenderAsLines());
    }

    /// <summary>
    /// 关闭向导并恢复输入。
    /// </summary>
    public void DismissQuestionWizard()
    {
        if (_activeQuestionWizard is null) return;

        // Complete waiters fail-closed so a replaced/torn-down wizard cannot hang.
        _activeQuestionWizard.CancelWizard();
        _activeQuestionWizard = null;
        _transcript.MessageView.EndTailRegion();
        if (_chatInput.IsQuestionMode)
            _chatInput.ClearQuestionMode();
        _chatInput.SetInteractionSuspended(false);
        FocusChatInput();
    }

    /// <summary>
    /// Tears down whichever session is showing. Tail region is a single slot,
    /// so Show* must never leave both pointers non-null.
    /// </summary>
    private void DismissActiveSession()
    {
        DismissInlineSelector();
        DismissQuestionWizard();
    }

    private void RefreshQuestionWizard()
    {
        if (_activeQuestionWizard is null) return;
        _transcript.MessageView.ReplaceTailRegion(_activeQuestionWizard.RenderAsLines());
        UpdateQuestionWizardInputState();
    }

    private void UpdateQuestionWizardInputState()
    {
        if (_activeQuestionWizard is null)
            return;

        // Choice questions fully suspend text editing and consume navigation keys.
        // Text questions keep the prompt editable; question mode prevents normal chat submission.
        _chatInput.SetInteractionSuspended(!_activeQuestionWizard.CurrentQuestion.IsTextType);
    }

    private void RefreshInlineSelector()
    {
        if (_activeInlineSelector is null) return;
        _transcript.MessageView.ReplaceTailRegion(InlineSelector.RenderAsLines(
            _activeInlineSelector.Title,
            _activeInlineSelector.Options,
            _activeInlineSelector.SelectedIndex,
            _activeInlineSelector.Prompt,
            _activeInlineSelector.UseInformationRequestCard));
    }

    // ── IInteractionSession ──────────────────────────────────────────────

    /// <summary>
    /// 交互会话按键入口 — ChatInputView 在提问/挂起期间转发到这里，
    /// ReplShell.OnKeyDown 兜底路径复用同一实现，消除重复的向导/选择器分发。
    /// </summary>
    public bool HandleInteractionKey(Key kb)
    {
        // 提问向导优先
        if (_activeQuestionWizard is { } wizard)
        {
            // 文本题：Editor 持有焦点，仅导航/取消组合键到达，其余留给输入
            if (_chatInput.IsQuestionMode)
                return HandleQuestionTextInputKey(wizard, kb);

            // 选择题：挂起态，全部键先经向导
            if (kb == Key.Esc)
            {
                wizard.CancelWizard();
                return true;
            }
            if (wizard.HandleKey(kb))
            {
                RefreshQuestionWizard();
                // 推进后当前题可能变为文本题 — 立即进入输入模式，否则输入框会
                // 回落到普通聊天提交路径（Enter 误发消息）
                EnterTextModeForCurrentQuestion();
                return true;
            }
        }

        if (_activeInlineSelector is { } selector && selector.HandleKey(kb))
        {
            RefreshInlineSelector();
            return true;
        }

        return false;
    }

    /// <summary>文本题输入期间的组合键处理（Esc 取消 / Enter 家族导航）。</summary>
    private bool HandleQuestionTextInputKey(QuestionWizard wizard, Key kb)
    {
        var bare = kb.NoShift.NoCtrl.NoAlt;

        if (bare == Key.Esc)
        {
            wizard.CancelWizard();
            _chatInput.ClearQuestionMode();
            return true;
        }

        if (wizard.IsLongTextMode)
        {
            // 长文本：Ctrl+Enter 提交并前进，Ctrl+Shift+Enter 回到上一题
            // （Enter 本身留给输入框换行）
            if (kb == Key.Enter.WithCtrl || kb == Key.Enter.WithCtrl.WithShift)
            {
                wizard.SetTextAnswer(_chatInput.CurrentText.Trim());
                _chatInput.ClearQuestionMode();
                wizard.HandleKey(kb);
                RefreshQuestionWizard();
                EnterTextModeForCurrentQuestion();
                return true;
            }
            return false;
        }

        // 短文本：Shift+Enter 上一题，Alt+←/→ 跨题导航
        if (bare == Key.Enter && kb.IsShift)
        {
            MoveToPreviousTextQuestion();
            return true;
        }
        if (kb.IsAlt && !kb.IsCtrl && !kb.IsShift && (bare == Key.CursorLeft || bare == Key.CursorRight))
        {
            MoveToTextQuestion(moveToPrevious: bare == Key.CursorLeft);
            return true;
        }

        return false;
    }

    /// <summary>chat:newline 映射在提问模式触发 — 短文本题回到上一题。</summary>
    public void HandleQuestionNewline() => MoveToPreviousTextQuestion();

    // Question Wizard text input

    /// <summary>
    /// 短文本题回到上一题 — 保存当前答案，重新渲染并进入上一题输入模式。
    /// 首题不动作。
    /// </summary>
    private void MoveToPreviousTextQuestion()
    {
        if (_activeQuestionWizard is not { } wizard || wizard.IsFirstQuestion)
            return;

        wizard.SetTextAnswer(_chatInput.CurrentText.Trim());
        _chatInput.ClearQuestionMode();
        wizard.MoveToPrevious();
        RefreshQuestionWizard();
        EnterTextModeForCurrentQuestion();
    }

    /// <summary>
    /// 文本题 Alt+←/→ — 保存当前答案并在问题间移动（边界处不动作）。
    /// </summary>
    private void MoveToTextQuestion(bool moveToPrevious)
    {
        if (_activeQuestionWizard is not { } wizard)
            return;
        if (moveToPrevious && wizard.IsFirstQuestion)
            return;
        if (!moveToPrevious && wizard.IsLastQuestion)
            return;

        wizard.SetTextAnswer(_chatInput.CurrentText.Trim());
        _chatInput.ClearQuestionMode();
        if (moveToPrevious)
            wizard.MoveToPrevious();
        else
            wizard.MoveToNext();
        RefreshQuestionWizard();
        EnterTextModeForCurrentQuestion();
    }

    /// <summary>
    /// 根据当前问题类型进入文本输入模式（短文本 / 长文本），选择题不进入。
    /// 短文本提交后自动进入下一题的输入模式；长文本恢复之前输入的内容，
    /// 由 Ctrl+Enter 导航路径提交。
    /// </summary>
    public void EnterTextModeForCurrentQuestion()
    {
        if (_activeQuestionWizard is not { } wizard) return;
        var question = wizard.CurrentQuestion;
        if (!question.IsTextType) return;

        var longText = question.Type == QuestionType.LongText;

        _chatInput.SetQuestionMode(answer =>
        {
            if (longText) return; // 长文本答案由 Ctrl+Enter 导航路径提交
            _app.Invoke(() =>
            {
                _activeQuestionWizard?.SetTextAnswer(answer);
                _chatInput.ClearQuestionMode();
                _activeQuestionWizard?.HandleKey(Key.Enter);
                RefreshQuestionWizard();
                EnterTextModeForCurrentQuestion();
            });
        }, longText);

        if (longText && !string.IsNullOrEmpty(question.Answer))
            _chatInput.SetText(question.Answer);
    }
}
