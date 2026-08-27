namespace OneCode.App.Tui;

/// <summary>
/// Renders the shared visual shell for every user-information request.
/// Scenario-specific controls are appended by InlineSelector or QuestionWizard.
/// </summary>
internal static class QuestionCardRenderer
{
    public static List<FormattedLine> RenderHeader(
        string title,
        string? prompt = null,
        int? currentQuestion = null,
        int? totalQuestions = null,
        string? typeLabel = null,
        string? description = null,
        int viewWidth = TuiSpacing.DefaultContentWidth)
    {
        var lines = new List<FormattedLine>
        {
            FormattedLine.Plain("", TuiPalette.BgPrimary),
        };

        var titleSegments = new List<LineSegment>
        {
            new("  ", TuiPalette.BgPrimary),
            new($"{TuiGlyphs.BarQuote} ", TuiPalette.Accent),
            new("需要补充信息", TuiPalette.Accent),
        };
        if (!string.Equals(title, "需要补充信息", StringComparison.Ordinal))
            titleSegments.Add(new($"  ·  {title}", TuiPalette.FgSecondary));
        if (currentQuestion is not null && totalQuestions is not null)
            titleSegments.Add(new($"  {currentQuestion}/{totalQuestions}", TuiPalette.FgMuted));
        lines.Add(FormattedLine.FromSegments(titleSegments.ToArray()));

        if (!string.IsNullOrWhiteSpace(prompt))
        {
            // 对话视图渲染时按 contentWidth 截断不换行，长问题必须在卡片渲染期
            // 按 viewWidth 预换行（与 ChatBlockRenderers.AddWrappedField 同一模式）。
            const string indent = "    ";
            var typePrefix = string.IsNullOrWhiteSpace(typeLabel) ? string.Empty : $"[{typeLabel}] ";
            var available = Math.Max(8, viewWidth - indent.Length - TextWidthHelper.GetDisplayWidth(typePrefix));
            var wrapped = TextWidthHelper.WordWrapByWidth(prompt, available);

            var firstSegments = new List<LineSegment>
            {
                new(indent, TuiPalette.BgPrimary),
            };
            if (typePrefix.Length > 0)
                firstSegments.Add(new(typePrefix, TuiPalette.FgMuted));
            firstSegments.Add(new(wrapped.Count > 0 ? wrapped[0] : prompt, TuiPalette.FgPrimary));
            lines.Add(FormattedLine.FromSegments(firstSegments.ToArray()));

            var continuation = indent + new string(' ', TextWidthHelper.GetDisplayWidth(typePrefix));
            foreach (var line in wrapped.Skip(1))
                lines.Add(FormattedLine.Plain(continuation + line, TuiPalette.FgPrimary));
        }

        if (!string.IsNullOrWhiteSpace(description))
        {
            const string descIndent = "    ";
            var descAvailable = Math.Max(8, viewWidth - descIndent.Length);
            var wrappedDescription = TextWidthHelper.WordWrapByWidth(description, descAvailable);
            if (wrappedDescription.Count == 0)
                wrappedDescription = [description];
            foreach (var line in wrappedDescription)
                lines.Add(FormattedLine.Plain(descIndent + line, TuiPalette.FgMuted));
        }

        lines.Add(FormattedLine.Plain("", TuiPalette.BgPrimary));
        return lines;
    }
}
