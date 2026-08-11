namespace OneCode.App.Tui;

public sealed class DiffView : View
{
    // 颜色从 TuiPalette 集中管理，参考 code.pen 设计
    private static readonly Color AddedColor = TuiPalette.DiffAdded;
    private static readonly Color RemovedColor = TuiPalette.DiffRemoved;
    private static readonly Color HunkColor = TuiPalette.DiffHunk;
    private static readonly Color ContextColor = TuiPalette.DiffContext;
    private static readonly Color PrefixColor = TuiPalette.FgMuted;
    private static readonly Color FileHeaderColor = TuiPalette.Accent;

    private readonly List<DiffLine> _lines = new();
    private int _scrollOffset;

    public DiffView()
    {
        Width = Dim.Fill();
        Height = Dim.Fill();
        CanFocus = true;
    }

    public void SetDiff(string diffText)
    {
        _lines.Clear();
        _scrollOffset = 0;

        if (string.IsNullOrEmpty(diffText))
        {
            _lines.Add(new DiffLine(DiffType.Context, "（无变更）"));
            SetNeedsDraw();
            return;
        }

        var rawLines = diffText.Replace("\r\n", "\n").Split('\n');

        foreach (var raw in rawLines)
        {
            if (raw.StartsWith("+++", StringComparison.Ordinal) || raw.StartsWith("---", StringComparison.Ordinal))
                _lines.Add(new DiffLine(DiffType.Hunk, raw));
            else if (raw.StartsWith("@@", StringComparison.Ordinal))
                _lines.Add(new DiffLine(DiffType.Hunk, raw));
            else if (raw.StartsWith('+'))
                _lines.Add(new DiffLine(DiffType.Added, raw));
            else if (raw.StartsWith('-'))
                _lines.Add(new DiffLine(DiffType.Removed, raw));
            else
                _lines.Add(new DiffLine(DiffType.Context, raw));
        }

        SetNeedsDraw();
    }

    public void SetUnifiedDiff(IReadOnlyList<DiffHunk> hunks)
    {
        _lines.Clear();
        _scrollOffset = 0;

        if (hunks.Count == 0)
        {
            _lines.Add(new DiffLine(DiffType.Context, "（无变更）"));
            SetNeedsDraw();
            return;
        }

        foreach (var hunk in hunks)
        {
            _lines.Add(new DiffLine(DiffType.Hunk,
                $"@@ -{hunk.OldStart},{hunk.OldCount} +{hunk.NewStart},{hunk.NewCount} @@"));

            foreach (var segment in hunk.Segments)
            {
                var type = segment.Type switch
                {
                    DiffSegmentType.Added => DiffType.Added,
                    DiffSegmentType.Removed => DiffType.Removed,
                    _ => DiffType.Context
                };

                var prefix = segment.Type switch
                {
                    DiffSegmentType.Added => "+",
                    DiffSegmentType.Removed => "-",
                    _ => " "
                };

                _lines.Add(new DiffLine(type, $"{prefix}{segment.Text}"));
            }
        }

        SetNeedsDraw();
    }

    public void Clear()
    {
        _lines.Clear();
        _scrollOffset = 0;
        SetNeedsDraw();
    }

    protected override bool OnKeyDown(Key kb)
    {
        var vp = Viewport;
        var pageSize = Math.Max(1, vp.Height - 1);
        var maxOffset = Math.Max(0, _lines.Count - vp.Height);

        if (kb == Key.CursorUp) { Scroll(-1); return true; }
        if (kb == Key.CursorDown) { Scroll(1); return true; }
        if (kb == Key.PageUp) { Scroll(-pageSize); return true; }
        if (kb == Key.PageDown) { Scroll(pageSize); return true; }
        if (kb == Key.Home) { _scrollOffset = 0; SetNeedsDraw(); return true; }
        if (kb == Key.End) { _scrollOffset = maxOffset; SetNeedsDraw(); return true; }
        // Vim-style bindings (when not in text input context)
        if (kb == Key.J) { Scroll(1); return true; }
        if (kb == Key.K) { Scroll(-1); return true; }

        return base.OnKeyDown(kb);
    }

    private void Scroll(int delta)
    {
        var vp = Viewport;
        var maxOffset = Math.Max(0, _lines.Count - vp.Height);
        _scrollOffset = Math.Clamp(_scrollOffset + delta, 0, maxOffset);
        SetNeedsDraw();
    }

    protected override bool OnDrawingContent(DrawContext? context)
    {
        base.OnDrawingContent(context);

        var vp = Viewport;
        if (vp.Width <= 0 || vp.Height <= 0)
            return true;

        // Match overlay card background so hunks stay readable inside DiffDetailOverlay.
        var bg = TuiPalette.BgCard;
        for (var clearRow = 0; clearRow < vp.Height; clearRow++)
        {
            Move(0, clearRow);
            SetAttribute(new Attribute(ContextColor, bg));
            AddStr(new string(' ', vp.Width));
        }

        var end = Math.Min(_scrollOffset + vp.Height, _lines.Count);
        var row = 0;

        for (var i = _scrollOffset; i < end; i++, row++)
        {
            var diffLine = _lines[i];
            Move(0, row);

            var color = diffLine.Type switch
            {
                DiffType.Added => AddedColor,
                DiffType.Removed => RemovedColor,
                DiffType.Hunk => HunkColor,
                _ => ContextColor
            };

            SetAttribute(new Attribute(color, bg));
            AddStr(Truncate(diffLine.Text, vp.Width));
        }

        if (_lines.Count > vp.Height && _scrollOffset + vp.Height < _lines.Count)
        {
            var remaining = _lines.Count - (_scrollOffset + vp.Height);
            Move(0, vp.Height - 1);
            SetAttribute(new Attribute(PrefixColor, bg));
            AddStr(Truncate($"{TuiGlyphs.ArrowDown} 还有 {remaining} 行  ({TuiGlyphs.ArrowUp}{TuiGlyphs.ArrowDown} / PgUp PgDn / Home End 滚动)", vp.Width));
        }

        return true;
    }

    private static string Truncate(string text, int maxWidth)
    {
        if (maxWidth <= 0) return "";
        if (text.Length <= maxWidth) return text;
        return text[..(maxWidth - 1)] + "\u2026";
    }

    private readonly record struct DiffLine(DiffType Type, string Text);

    private enum DiffType { Context, Added, Removed, Hunk }
}

public enum DiffSegmentType { Context, Added, Removed }

public readonly record struct DiffSegment(DiffSegmentType Type, string Text);

public sealed class DiffHunk
{
    public int OldStart { get; set; }
    public int OldCount { get; set; }
    public int NewStart { get; set; }
    public int NewCount { get; set; }
    public List<DiffSegment> Segments { get; set; } = new();
}
