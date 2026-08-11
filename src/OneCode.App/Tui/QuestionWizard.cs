namespace OneCode.App.Tui;

/// <summary>
/// 多问题向导组件 — 支持多种题型（单选、多选、短文本、长文本、确认题），可前后导航。
/// 类似 InlineSelector，但支持多问题和更复杂的导航。
/// </summary>
public sealed class QuestionWizard
{
    private readonly string _title;
    private readonly List<WizardQuestion> _questions;
    private int _currentIndex;
    private int _selectedOptionIndex;
    private readonly TaskCompletionSource<WizardResult> _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    // 多选题的选中状态
    private readonly HashSet<int> _selectedMultipleOptions = new();

    public QuestionWizard(string title, IReadOnlyList<WizardQuestion> questions, int startIndex = 0)
    {
        _title = title;
        _questions = questions.ToList();
        _currentIndex = Math.Clamp(startIndex, 0, _questions.Count - 1);
        _selectedOptionIndex = 0;
    }

    public string Title => _title;
    public IReadOnlyList<WizardQuestion> Questions => _questions;
    public int CurrentIndex => _currentIndex;
    public WizardQuestion CurrentQuestion => _questions[_currentIndex];
    public int TotalQuestions => _questions.Count;
    public bool IsFirstQuestion => _currentIndex == 0;
    public bool IsLastQuestion => _currentIndex == _questions.Count - 1;
    public bool HasAnsweredAll => _questions.All(q =>
        q.Type == QuestionType.MultipleChoice ? q.MultipleAnswers.Count > 0 : !string.IsNullOrEmpty(q.Answer));
    public Task<WizardResult> ResultTask => _tcs.Task;

    /// <summary>当前问题是否为长文本输入模式（需要外部输入框）。</summary>
    public bool IsLongTextMode => CurrentQuestion.Type == QuestionType.LongText;

    /// <summary>处理按键输入，返回是否消耗了按键。</summary>
    public bool HandleKey(Key kb)
    {
        var question = CurrentQuestion;

        return question.Type switch
        {
            QuestionType.SingleChoice or QuestionType.Confirm => HandleSingleChoice(kb),
            QuestionType.MultipleChoice => HandleMultipleChoice(kb),
            QuestionType.ShortText => HandleShortTextNavigation(kb),
            QuestionType.LongText => HandleLongTextNavigation(kb),
            _ => false
        };
    }

    private bool HandleShortTextNavigation(Key kb)
    {
        // 短文本题使用 Enter 确认并下一题，Shift+Enter 上一题
        if (kb == Key.Enter)
        {
            MoveToNextOrFinish();
            return true;
        }

        if (kb == Key.Enter.WithShift)
        {
            MoveToPrevious();
            return true;
        }

        return false;
    }

    private bool HandleLongTextNavigation(Key kb)
    {
        // 长文本题使用 Ctrl+Enter 确认并下一题，Ctrl+Shift+Enter 上一题
        // 因为 Enter 需要在输入框内换行
        if (kb == Key.Enter.WithCtrl)
        {
            MoveToNextOrFinish();
            return true;
        }

        if (kb == Key.Enter.WithCtrl.WithShift)
        {
            MoveToPrevious();
            return true;
        }

        return false;
    }

    private bool HandleSingleChoice(Key kb)
    {
        var options = GetEffectiveOptions();

        if (kb == Key.CursorUp)
        {
            if (_selectedOptionIndex > 0) _selectedOptionIndex--;
            return true;
        }

        if (kb == Key.CursorDown)
        {
            if (_selectedOptionIndex < options.Count - 1) _selectedOptionIndex++;
            return true;
        }

        if (kb == Key.Enter)
        {
            SelectSingleOption();
            MoveToNextOrFinish();
            return true;
        }

        if (kb == Key.CursorLeft && !IsFirstQuestion)
        {
            MoveToPrevious();
            return true;
        }

        if (kb == Key.CursorRight && !IsLastQuestion)
        {
            MoveToNext();
            return true;
        }

        return false;
    }

    private bool HandleMultipleChoice(Key kb)
    {
        var options = CurrentQuestion.Options!;

        if (kb == Key.CursorUp)
        {
            if (_selectedOptionIndex > 0) _selectedOptionIndex--;
            return true;
        }

        if (kb == Key.CursorDown)
        {
            if (_selectedOptionIndex < options.Count - 1) _selectedOptionIndex++;
            return true;
        }

        if (kb == Key.Space || kb == Key.Enter)
        {
            // 切换当前选项的选中状态
            if (_selectedMultipleOptions.Contains(_selectedOptionIndex))
                _selectedMultipleOptions.Remove(_selectedOptionIndex);
            else
                _selectedMultipleOptions.Add(_selectedOptionIndex);
            return true;
        }

        if (kb == Key.CursorLeft && !IsFirstQuestion)
        {
            SaveMultipleChoiceAnswer();
            MoveToPrevious();
            return true;
        }

        if (kb == Key.CursorRight && !IsLastQuestion)
        {
            SaveMultipleChoiceAnswer();
            MoveToNext();
            return true;
        }

        if (kb == Key.Enter.WithCtrl)
        {
            // Ctrl+Enter 确认多选并继续
            SaveMultipleChoiceAnswer();
            MoveToNextOrFinish();
            return true;
        }

        return false;
    }

    private void SaveMultipleChoiceAnswer()
    {
        var question = CurrentQuestion;
        if (question.Type == QuestionType.MultipleChoice && question.Options != null)
        {
            question.MultipleAnswers.Clear();
            foreach (var idx in _selectedMultipleOptions.OrderBy(i => i))
            {
                if (idx < question.Options.Count)
                    question.MultipleAnswers.Add(question.Options[idx]);
            }
            // 同时保存为逗号分隔的字符串
            question.Answer = string.Join(", ", question.MultipleAnswers);
        }
    }

    private IReadOnlyList<string> GetEffectiveOptions()
    {
        var question = CurrentQuestion;
        return question.Type == QuestionType.Confirm
            ? WizardQuestion.ConfirmOptions
            : question.Options ?? new List<string>();
    }

    private void SelectSingleOption()
    {
        var question = CurrentQuestion;
        var options = GetEffectiveOptions();

        if (options.Count > 0 && _selectedOptionIndex < options.Count)
        {
            question.Answer = options[_selectedOptionIndex];
        }
    }

    private void MoveToNextOrFinish()
    {
        if (IsLastQuestion)
        {
            FinishWizard();
        }
        else
        {
            MoveToNext();
        }
    }

    public void MoveToPrevious()
    {
        if (_currentIndex > 0)
        {
            _currentIndex--;
            ResetSelectionForCurrentQuestion();
        }
    }

    public void MoveToNext()
    {
        if (_currentIndex < _questions.Count - 1)
        {
            _currentIndex++;
            ResetSelectionForCurrentQuestion();
        }
    }

    private void ResetSelectionForCurrentQuestion()
    {
        var question = CurrentQuestion;
        _selectedOptionIndex = 0;
        _selectedMultipleOptions.Clear();

        if (question.IsChoiceType && question.Options != null)
        {
            // 如果已有答案，选中对应选项
            if (!string.IsNullOrEmpty(question.Answer))
            {
                if (question.Type == QuestionType.MultipleChoice)
                {
                    // 多选题：恢复选中状态
                    for (var i = 0; i < question.Options.Count; i++)
                    {
                        if (question.MultipleAnswers.Contains(question.Options[i]))
                            _selectedMultipleOptions.Add(i);
                    }
                }
                else
                {
                    _selectedOptionIndex = question.Options.IndexOf(question.Answer);
                    if (_selectedOptionIndex < 0) _selectedOptionIndex = 0;
                }
            }
        }
    }

    public void FinishWizard()
    {
        // 确保当前问题有答案
        var currentQuestion = CurrentQuestion;
        if (currentQuestion.Type == QuestionType.MultipleChoice)
        {
            SaveMultipleChoiceAnswer();
        }
        else if (currentQuestion.IsChoiceType)
        {
            SelectSingleOption();
        }

        var answers = _questions.ToDictionary(
            q => q.Id,
            q => q.Answer ?? string.Empty);

        _tcs.TrySetResult(new WizardResult(answers));
    }

    public void CancelWizard()
    {
        _tcs.TrySetResult(WizardResult.Cancelled);
    }

    /// <summary>
    /// 设置文本题答案（短文本或长文本）。
    /// </summary>
    public void SetTextAnswer(string answer)
    {
        var question = CurrentQuestion;
        if (question.IsTextType)
        {
            question.Answer = answer;
        }
    }

    /// <summary>
    /// 渲染向导为 FormattedLines，用于在对话视图中显示。
    /// </summary>
    public IReadOnlyList<FormattedLine> RenderAsLines()
    {
        var question = CurrentQuestion;
        var lines = QuestionCardRenderer.RenderHeader(
            _title,
            question.Question,
            _currentIndex + 1,
            TotalQuestions,
            GetQuestionTypeLabel(question.Type),
            question.Description);

        // 根据题型渲染不同UI
        switch (question.Type)
        {
            case QuestionType.SingleChoice:
            case QuestionType.Confirm:
                RenderSingleChoice(lines, question);
                break;
            case QuestionType.MultipleChoice:
                RenderMultipleChoice(lines, question);
                break;
            case QuestionType.ShortText:
                RenderShortText(lines, question);
                break;
            case QuestionType.LongText:
                RenderLongText(lines, question);
                break;
        }

        // 空行
        lines.Add(FormattedLine.Plain("", TuiPalette.BgPrimary));

        // 导航提示
        RenderNavigationHints(lines, question);

        return lines;
    }

    private static string GetQuestionTypeLabel(QuestionType type) => type switch
    {
        QuestionType.SingleChoice => "单选",
        QuestionType.MultipleChoice => "多选",
        QuestionType.ShortText => "简答",
        QuestionType.LongText => "文档",
        QuestionType.Confirm => "确认",
        _ => "问答"
    };

    private void RenderSingleChoice(List<FormattedLine> lines, WizardQuestion question)
    {
        var options = GetEffectiveOptions();
        for (var i = 0; i < options.Count; i++)
        {
            var isSelected = i == _selectedOptionIndex;
            var isAnswered = question.Answer == options[i];
            var bullet = isSelected ? TuiGlyphs.RoleBullet : (isAnswered ? "◆" : TuiGlyphs.Pending);
            var labelColor = isSelected ? TuiPalette.Accent : (isAnswered ? TuiPalette.Success : TuiPalette.FgPrimary);

            lines.Add(FormattedLine.FromSegments(new[]
            {
                new LineSegment("  ", TuiPalette.BgPrimary),
                new LineSegment($"{bullet} ", isSelected ? TuiPalette.Accent : TuiPalette.FgMuted),
                new LineSegment(options[i], labelColor),
            }));
        }
    }

    private void RenderMultipleChoice(List<FormattedLine> lines, WizardQuestion question)
    {
        if (question.Options == null) return;

        for (var i = 0; i < question.Options.Count; i++)
        {
            var isSelected = i == _selectedOptionIndex;
            var isChecked = _selectedMultipleOptions.Contains(i);
            var isAnswered = question.MultipleAnswers.Contains(question.Options[i]);

            var checkbox = isChecked ? "[✓]" : "[ ]";
            var bullet = isSelected ? ">" : " ";
            var labelColor = isChecked ? TuiPalette.Success : (isSelected ? TuiPalette.Accent : TuiPalette.FgPrimary);

            lines.Add(FormattedLine.FromSegments(new[]
            {
                new LineSegment("  ", TuiPalette.BgPrimary),
                new LineSegment($"{bullet}{checkbox} ", isSelected ? TuiPalette.Accent : TuiPalette.FgMuted),
                new LineSegment(question.Options[i], labelColor),
            }));
        }

        // 显示已选数量提示
        if (_selectedMultipleOptions.Count > 0)
        {
            lines.Add(FormattedLine.FromSegments(new[]
            {
                new LineSegment("  ", TuiPalette.BgPrimary),
                new LineSegment($"已选择 {_selectedMultipleOptions.Count} 项", TuiPalette.FgMuted),
            }));
        }
    }

    private static void RenderShortText(List<FormattedLine> lines, WizardQuestion question)
    {
        var currentAnswer = question.Answer ?? string.Empty;
        var displayText = string.IsNullOrEmpty(currentAnswer)
            ? "[请输入简短回答，按 Enter 继续]"
            : $"> {currentAnswer}";
        var color = string.IsNullOrEmpty(currentAnswer) ? TuiPalette.FgMuted : TuiPalette.Accent;

        lines.Add(FormattedLine.FromSegments(new[]
        {
            new LineSegment("  ", TuiPalette.BgPrimary),
            new LineSegment(displayText, color),
        }));
    }

    private static void RenderLongText(List<FormattedLine> lines, WizardQuestion question)
    {
        var currentAnswer = question.Answer ?? string.Empty;

        if (string.IsNullOrEmpty(currentAnswer))
        {
            lines.Add(FormattedLine.FromSegments(new[]
            {
                new LineSegment("  ", TuiPalette.BgPrimary),
                new LineSegment("[请在下方输入详细内容...]", TuiPalette.FgMuted),
            }));
        }
        else
        {
            // 显示已输入内容的预览（前3行）
            var previewLines = currentAnswer.Split('\n').Take(3).ToList();
            foreach (var previewLine in previewLines)
            {
                lines.Add(FormattedLine.FromSegments(new[]
                {
                    new LineSegment("  ", TuiPalette.BgPrimary),
                    new LineSegment("> " + previewLine, TuiPalette.Accent),
                }));
            }

            if (currentAnswer.Split('\n').Length > 3)
            {
                lines.Add(FormattedLine.FromSegments(new[]
                {
                    new LineSegment("  ", TuiPalette.BgPrimary),
                    new LineSegment($"  ... 还有 {currentAnswer.Split('\n').Length - 3} 行", TuiPalette.FgMuted),
                }));
            }
        }
    }

    private void RenderNavigationHints(List<FormattedLine> lines, WizardQuestion question)
    {
        var navHints = new List<LineSegment>
        {
            new("  ", TuiPalette.BgPrimary),
        };

        if (!IsFirstQuestion && !question.IsTextType)
        {
            navHints.Add(new LineSegment("← 上一题", TuiPalette.FgSecondary));
            navHints.Add(new LineSegment(" · ", TuiPalette.FgMuted));
        }
        else if (!IsFirstQuestion && question.IsTextType)
        {
            navHints.Add(new LineSegment("Alt+← 上一题", TuiPalette.FgSecondary));
            navHints.Add(new LineSegment(" · ", TuiPalette.FgMuted));
        }

        switch (question.Type)
        {
            case QuestionType.SingleChoice:
            case QuestionType.Confirm:
                navHints.Add(new LineSegment("↑↓ 选择", TuiPalette.FgSecondary));
                navHints.Add(new LineSegment(" · ", TuiPalette.FgMuted));
                navHints.Add(new LineSegment("Enter 确认", TuiPalette.FgSecondary));
                break;

            case QuestionType.MultipleChoice:
                navHints.Add(new LineSegment("↑↓ 移动", TuiPalette.FgSecondary));
                navHints.Add(new LineSegment(" · ", TuiPalette.FgMuted));
                navHints.Add(new LineSegment("Space/Enter 选择", TuiPalette.FgSecondary));
                navHints.Add(new LineSegment(" · ", TuiPalette.FgMuted));
                navHints.Add(new LineSegment("Ctrl+Enter 继续", TuiPalette.FgSecondary));
                break;

            case QuestionType.ShortText:
                navHints.Add(new LineSegment("Enter 继续", TuiPalette.FgSecondary));
                navHints.Add(new LineSegment(" · ", TuiPalette.FgMuted));
                navHints.Add(new LineSegment("Shift+Enter 上一题", TuiPalette.FgSecondary));
                break;

            case QuestionType.LongText:
                navHints.Add(new LineSegment("Ctrl+Enter 继续", TuiPalette.FgSecondary));
                navHints.Add(new LineSegment(" · ", TuiPalette.FgMuted));
                navHints.Add(new LineSegment("Ctrl+Shift+Enter 上一题", TuiPalette.FgSecondary));
                navHints.Add(new LineSegment(" · ", TuiPalette.FgMuted));
                navHints.Add(new LineSegment("Enter 换行", TuiPalette.FgMuted));
                break;
        }

        if (!IsLastQuestion)
        {
            navHints.Add(new LineSegment(" · ", TuiPalette.FgMuted));
            navHints.Add(new LineSegment(
                question.IsTextType ? "Alt+→ 下一题" : "→ 下一题",
                TuiPalette.FgSecondary));
        }
        else if (HasAnsweredAll)
        {
            navHints.Add(new LineSegment(" · ", TuiPalette.FgMuted));
            navHints.Add(new LineSegment("Enter/Ctrl+Enter 完成", TuiPalette.Success));
        }

        navHints.Add(new LineSegment(" · ", TuiPalette.FgMuted));
        navHints.Add(new LineSegment("Esc 取消", TuiPalette.FgMuted));

        lines.Add(FormattedLine.FromSegments(navHints.ToArray()));
        lines.Add(FormattedLine.Plain("", TuiPalette.BgPrimary));
    }
}
