namespace OneCode.App.Tui;

/// <summary>
/// An inline selector that renders options directly within the conversation view.
/// Users navigate with ↑↓ arrows and confirm with Enter, dismiss with Esc.
/// Replaces modal overlays for tool permission prompts and other confirmations.
/// </summary>
public sealed class InlineSelector
{
    private readonly string _title;
    private readonly IReadOnlyList<InlineSelectorOption> _options;
    private readonly string? _prompt;
    private readonly bool _useInformationRequestCard;
    private int _selectedIndex;
    private readonly TaskCompletionSource<InlineSelectorResult> _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public InlineSelector(
        string title,
        IReadOnlyList<InlineSelectorOption> options,
        int defaultIndex = 0,
        string? prompt = null,
        bool useInformationRequestCard = false)
    {
        _title = title;
        _options = options;
        _prompt = prompt;
        _useInformationRequestCard = useInformationRequestCard;
        _selectedIndex = Math.Clamp(defaultIndex, 0, options.Count - 1);
    }

    public string Title => _title;
    public IReadOnlyList<InlineSelectorOption> Options => _options;
    public string? Prompt => _prompt;
    public bool UseInformationRequestCard => _useInformationRequestCard;
    public int SelectedIndex => _selectedIndex;
    public Task<InlineSelectorResult> ResultTask => _tcs.Task;

    /// <summary>Handle a key press. Returns true if key was consumed.</summary>
    public bool HandleKey(Key kb)
    {
        if (kb == Key.CursorUp)
        {
            if (_selectedIndex > 0) _selectedIndex--;
            return true;
        }

        if (kb == Key.CursorDown)
        {
            if (_selectedIndex < _options.Count - 1) _selectedIndex++;
            return true;
        }

        if (kb == Key.Enter)
        {
            _tcs.TrySetResult(new InlineSelectorResult(_selectedIndex, _options[_selectedIndex].Id));
            return true;
        }

        if (kb == Key.Esc)
        {
            _tcs.TrySetResult(InlineSelectorResult.Dismissed);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Renders the selector state as FormattedLines for embedding in the conversation view
    /// instead of using a separate View. Used by ChatTranscriptView.ShowInlineSelector.
    /// </summary>
    public static IReadOnlyList<FormattedLine> RenderAsLines(
        string title,
        IReadOnlyList<InlineSelectorOption> options,
        int selectedIndex,
        string? prompt = null,
        bool useInformationRequestCard = false)
    {
        var lines = useInformationRequestCard
            ? QuestionCardRenderer.RenderHeader(title, prompt)
            : RenderStandardHeader(title, prompt);

        // Options — bullet + label + description on same line
        for (var i = 0; i < options.Count; i++)
        {
            var isSelected = i == selectedIndex;
            var bullet = isSelected ? TuiGlyphs.RoleBullet : TuiGlyphs.Pending;
            var labelColor = isSelected ? TuiPalette.Accent : TuiPalette.FgPrimary;

            var segs = new List<LineSegment>
            {
                new("  ", TuiPalette.BgPrimary),
                new($"{bullet} ", isSelected ? TuiPalette.Accent : TuiPalette.FgMuted),
                new(options[i].Label, labelColor),
            };

            if (options[i].Description is { Length: > 0 } desc)
                segs.Add(new($"  {desc}", TuiPalette.FgMuted));

            lines.Add(FormattedLine.FromSegments(segs.ToArray()));
        }

        // Hints
        lines.Add(FormattedLine.Plain("", TuiPalette.BgPrimary));
        lines.Add(FormattedLine.FromSegments(new[]
        {
            new LineSegment($"  {TuiGlyphs.ArrowUp}{TuiGlyphs.ArrowDown} ", TuiPalette.FgSecondary),
            new LineSegment("选择", TuiPalette.FgMuted),
            new LineSegment(" · ", TuiPalette.FgMuted),
            new LineSegment("Enter", TuiPalette.FgSecondary),
            new LineSegment(" 确认", TuiPalette.FgMuted),
            new LineSegment(" · ", TuiPalette.FgMuted),
            new LineSegment("Esc", TuiPalette.FgSecondary),
            new LineSegment(" 取消", TuiPalette.FgMuted),
        }));

        return lines;
    }

    private static List<FormattedLine> RenderStandardHeader(string title, string? prompt)
    {
        var lines = new List<FormattedLine>
        {
            FormattedLine.Plain("", TuiPalette.BgPrimary),
            FormattedLine.FromSegments(new[]
            {
                new LineSegment("  ", TuiPalette.BgPrimary),
                new LineSegment(title, TuiPalette.Warning),
            }),
        };
        if (!string.IsNullOrWhiteSpace(prompt))
            lines.Add(FormattedLine.Plain($"  {prompt}", TuiPalette.FgPrimary));
        lines.Add(FormattedLine.Plain("", TuiPalette.BgPrimary));
        return lines;
    }
}

public sealed record InlineSelectorOption(string Id, string Label, string? Description = null);

public sealed record InlineSelectorResult(int SelectedIndex, string SelectedId)
{
    public static readonly InlineSelectorResult Dismissed = new(-1, "dismissed");
    public bool IsDismissed => SelectedIndex < 0;
}
