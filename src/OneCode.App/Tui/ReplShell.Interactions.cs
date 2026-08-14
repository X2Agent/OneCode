namespace OneCode.App.Tui;

/// <summary>
/// InlineSelector and QuestionWizard lifecycle management extracted from ReplShell.
/// Manages the show/dismiss/refresh cycle for inline selector overlays (permission prompts)
/// and multi-question interactive wizards within the conversation view.
/// </summary>
public sealed partial class ReplShell
{
    // Inline Selector (replaces modal overlays)
    private InlineSelector? _activeInlineSelector;
    private int _inlineSelectorLineCount;

    // Question Wizard (multi-question interactive flow)
    private QuestionWizard? _activeQuestionWizard;
    private int _questionWizardLineCount;

    public void ShowInlineSelector(InlineSelector selector)
    {
        DismissInlineSelector();
        _activeInlineSelector = selector;
        _chatInput.SetInteractionSuspended(true);

        // Render the selector as lines in the conversation view
        var lines = InlineSelector.RenderAsLines(
            selector.Title,
            selector.Options,
            selector.SelectedIndex,
            selector.Prompt,
            selector.UseInformationRequestCard);
        _inlineSelectorLineCount = lines.Count;
        _transcript.MessageView.AppendLines(lines);
    }

    public void DismissInlineSelector()
    {
        if (_activeInlineSelector is not null)
        {
            // Remove the selector lines from the conversation view
            if (_inlineSelectorLineCount > 0)
                _transcript.MessageView.ReplaceLastLines(_inlineSelectorLineCount, null);
            _activeInlineSelector = null;
            _inlineSelectorLineCount = 0;
            _chatInput.SetInteractionSuspended(false);
            FocusChatInput();
        }
    }

    /// <summary>
    /// 显示多问题向导，接管键盘输入直到向导完成或取消。
    /// </summary>
    public void ShowQuestionWizard(QuestionWizard wizard)
    {
        DismissQuestionWizard();
        _activeQuestionWizard = wizard;
        UpdateQuestionWizardInputState();

        // Render the wizard as lines in the conversation view
        var lines = wizard.RenderAsLines();
        _questionWizardLineCount = lines.Count;
        _transcript.MessageView.AppendLines(lines);
    }

    /// <summary>
    /// 关闭向导并恢复输入。
    /// </summary>
    public void DismissQuestionWizard()
    {
        if (_activeQuestionWizard is not null)
        {
            // Remove the wizard lines from the conversation view
            if (_questionWizardLineCount > 0)
                _transcript.MessageView.ReplaceLastLines(_questionWizardLineCount, null);
            _activeQuestionWizard = null;
            _questionWizardLineCount = 0;
            if (_chatInput.IsQuestionMode)
                _chatInput.ClearQuestionMode();
            _chatInput.SetInteractionSuspended(false);
            FocusChatInput();
        }
    }

    private void RefreshQuestionWizard()
    {
        if (_activeQuestionWizard is null) return;
        var lines = _activeQuestionWizard.RenderAsLines();
        _transcript.MessageView.ReplaceLastLines(_questionWizardLineCount, lines);
        _questionWizardLineCount = lines.Count;
        UpdateQuestionWizardInputState();
    }

    private void UpdateQuestionWizardInputState()
    {
        if (_activeQuestionWizard is null)
            return;

        // Choice questions fully suspend text editing and forward navigation keys.
        // Text questions keep the prompt editable; question mode prevents normal chat submission.
        _chatInput.SetInteractionSuspended(!_activeQuestionWizard.CurrentQuestion.IsTextType);
    }

    private void RefreshInlineSelector()
    {
        if (_activeInlineSelector is null) return;
        var lines = InlineSelector.RenderAsLines(
            _activeInlineSelector.Title,
            _activeInlineSelector.Options,
            _activeInlineSelector.SelectedIndex,
            _activeInlineSelector.Prompt,
            _activeInlineSelector.UseInformationRequestCard);
        _transcript.MessageView.ReplaceLastLines(_inlineSelectorLineCount, lines);
        _inlineSelectorLineCount = lines.Count;
    }

    /// <summary>
    /// Handles keys forwarded from ChatInputView while interaction is suspended
    /// (e.g. InlineSelector for permission prompts is active). Editor consumes
    /// all keys when it has focus, so this is the only path for arrow/Enter/Esc
    /// to reach the InlineSelector.
    /// </summary>
    private void OnQuestionPreviousRequested()
    {
        if (_activeQuestionWizard is null || _activeQuestionWizard.IsFirstQuestion)
            return;

        _activeQuestionWizard.SetTextAnswer(_chatInput.CurrentText.Trim());
        _chatInput.ClearQuestionMode();
        _activeQuestionWizard.MoveToPrevious();
        RefreshQuestionWizard();
        EnterTextModeForCurrentQuestion();
    }

    private void OnQuestionCancelRequested()
    {
        if (_activeQuestionWizard is null)
            return;

        _activeQuestionWizard.CancelWizard();
        _chatInput.ClearQuestionMode();
    }

    private void OnQuestionLongTextNavigationRequested(bool moveToPrevious)
    {
        if (_activeQuestionWizard is null || !_activeQuestionWizard.IsLongTextMode)
            return;

        _activeQuestionWizard.SetTextAnswer(_chatInput.CurrentText.Trim());
        _chatInput.ClearQuestionMode();
        _activeQuestionWizard.HandleKey(
            moveToPrevious ? Key.Enter.WithCtrl.WithShift : Key.Enter.WithCtrl);
        RefreshQuestionWizard();
        EnterTextModeForCurrentQuestion();
    }

    private void OnQuestionTextNavigationRequested(bool moveToPrevious)
    {
        if (_activeQuestionWizard is null || !_activeQuestionWizard.CurrentQuestion.IsTextType)
            return;
        if (moveToPrevious && _activeQuestionWizard.IsFirstQuestion)
            return;
        if (!moveToPrevious && _activeQuestionWizard.IsLastQuestion)
            return;

        _activeQuestionWizard.SetTextAnswer(_chatInput.CurrentText.Trim());
        _chatInput.ClearQuestionMode();
        if (moveToPrevious)
            _activeQuestionWizard.MoveToPrevious();
        else
            _activeQuestionWizard.MoveToNext();
        RefreshQuestionWizard();
        EnterTextModeForCurrentQuestion();
    }

    private void OnInteractionSuspendedKey(Key kb)
    {
        // Question wizard takes priority
        if (_activeQuestionWizard is not null)
        {
            // 长文本模式下，特殊按键处理
            if (_activeQuestionWizard.IsLongTextMode)
            {
                // Ctrl+Enter 提交
                if (kb == Key.Enter.WithCtrl)
                {
                    var answer = _chatInput.CurrentText.Trim();
                    _activeQuestionWizard.SetTextAnswer(answer);
                    _chatInput.ClearQuestionMode();
                    _activeQuestionWizard.HandleKey(kb);
                    RefreshQuestionWizard();
                    return;
                }

                // Ctrl+Shift+Enter 上一题 — 保持与 OnKeyDown 路径一致
                if (kb == Key.Enter.WithCtrl.WithShift)
                {
                    var answer = _chatInput.CurrentText.Trim();
                    _activeQuestionWizard.SetTextAnswer(answer);
                    _chatInput.ClearQuestionMode();
                    _activeQuestionWizard.HandleKey(kb);
                    RefreshQuestionWizard();
                    // 重新进入长文本模式（与 OnKeyDown 路径行为一致）
                    EnterLongTextModeIfNeeded();
                    return;
                }

                // Esc 取消
                if (kb == Key.Esc)
                {
                    _activeQuestionWizard.CancelWizard();
                    _chatInput.ClearQuestionMode();
                    return;
                }

                // 其他键让 ChatInputView 处理
                return;
            }

            if (kb == Key.Esc)
            {
                _activeQuestionWizard.CancelWizard();
                return;
            }

            if (_activeQuestionWizard.HandleKey(kb))
            {
                RefreshQuestionWizard();
                return;
            }
        }

        if (_activeInlineSelector is not null)
        {
            if (_activeInlineSelector.HandleKey(kb))
                RefreshInlineSelector();
        }
    }

    // Question Wizard Text Input Modes

    /// <summary>
    /// 进入短文本输入模式（供外部调用）。
    /// </summary>
    public void EnterShortTextModeForWizard()
    {
        if (_activeQuestionWizard is null) return;

        _chatInput.SetQuestionMode(_activeQuestionWizard.CurrentQuestion.Question, answer =>
        {
            _app.Invoke(() =>
            {
                _activeQuestionWizard?.SetTextAnswer(answer);
                _chatInput.ClearQuestionMode();
                _activeQuestionWizard?.HandleKey(Key.Enter);
                RefreshQuestionWizard();
                EnterTextModeForCurrentQuestion();
            });
        }, longText: false);
    }

    /// <summary>
    /// 进入长文本输入模式（供外部调用）。
    /// </summary>
    public void EnterLongTextModeForWizard()
    {
        if (_activeQuestionWizard is null) return;
        if (_activeQuestionWizard.CurrentQuestion.Type != QuestionType.LongText) return;

        // 恢复之前输入的内容
        var previousAnswer = _activeQuestionWizard.CurrentQuestion.Answer ?? "";
        _chatInput.SetQuestionMode(_activeQuestionWizard.CurrentQuestion.Question, answer =>
        {
            // 回调在 Ctrl+Enter 时触发，这里不需要额外处理
        }, longText: true);

        // 恢复之前的内容
        if (!string.IsNullOrEmpty(previousAnswer))
        {
            _chatInput.SetText(previousAnswer);
        }
    }

    /// <summary>
    /// 根据当前问题类型进入相应的文本输入模式。
    /// </summary>
    private void EnterTextModeForCurrentQuestion()
    {
        if (_activeQuestionWizard is null) return;

        var currentType = _activeQuestionWizard.CurrentQuestion.Type;
        if (currentType == QuestionType.ShortText)
        {
            EnterShortTextModeForWizard();
        }
        else if (currentType == QuestionType.LongText)
        {
            EnterLongTextModeForWizard();
        }
    }

    /// <summary>
    /// 进入短文本输入模式（内部使用，不触发自动导航）。
    /// </summary>
    private void EnterShortTextMode()
    {
        if (_activeQuestionWizard is null) return;

        _chatInput.SetQuestionMode(_activeQuestionWizard.CurrentQuestion.Question, answer =>
        {
            _app.Invoke(() =>
            {
                _activeQuestionWizard?.SetTextAnswer(answer);
                _chatInput.ClearQuestionMode();
                _activeQuestionWizard?.HandleKey(Key.Enter);
                RefreshQuestionWizard();
            });
        }, longText: false);
    }

    /// <summary>
    /// 如果需要，进入长文本输入模式（内部使用）。
    /// </summary>
    private void EnterLongTextModeIfNeeded()
    {
        if (_activeQuestionWizard is null) return;
        if (_activeQuestionWizard.CurrentQuestion.Type != QuestionType.LongText) return;

        // 恢复之前输入的内容
        var previousAnswer = _activeQuestionWizard.CurrentQuestion.Answer ?? "";
        _chatInput.SetQuestionMode(_activeQuestionWizard.CurrentQuestion.Question, answer =>
        {
            // 回调在 Ctrl+Enter 时触发
        }, longText: true);

        // 恢复之前的内容
        if (!string.IsNullOrEmpty(previousAnswer))
        {
            _chatInput.SetText(previousAnswer);
        }
    }
}
