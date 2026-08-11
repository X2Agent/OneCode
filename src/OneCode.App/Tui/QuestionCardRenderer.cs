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
        string? description = null)
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
            var promptSegments = new List<LineSegment>
            {
                new("    ", TuiPalette.BgPrimary),
            };
            if (!string.IsNullOrWhiteSpace(typeLabel))
                promptSegments.Add(new($"[{typeLabel}] ", TuiPalette.FgMuted));
            promptSegments.Add(new(prompt, TuiPalette.FgPrimary));
            lines.Add(FormattedLine.FromSegments(promptSegments.ToArray()));
        }

        if (!string.IsNullOrWhiteSpace(description))
            lines.Add(FormattedLine.Plain($"    {description}", TuiPalette.FgMuted));

        lines.Add(FormattedLine.Plain("", TuiPalette.BgPrimary));
        return lines;
    }
}
