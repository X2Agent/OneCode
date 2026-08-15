namespace OneCode.App.Tui;

/// <summary>
/// <see cref="QuestionWizard"/> 的渲染部分：把向导当前状态渲染为
/// <see cref="FormattedLine"/> 序列，嵌入对话视图尾部交互区域。
/// </summary>
public sealed partial class QuestionWizard
{
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
