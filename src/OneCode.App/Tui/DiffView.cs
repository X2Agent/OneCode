namespace OneCode.App.Tui;

using OneCode.Core.Keybindings;

/// <summary>
/// Unified-diff scroll view. All keybindings resolve through
/// <see cref="KeybindingResolver"/> in the <see cref="KeybindingDefaults.ContextDiff"/>
/// context (unioned into the active set at resolution time — no push/pop lifecycle
/// to leak); Esc-close stays a retained hardcoded overlay behavior.
/// </summary>
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
    private readonly KeybindingResolver _keyResolver;
    private readonly KeybindingContextManager _keyContextManager;

    public DiffView(KeybindingResolver? keyResolver = null, KeybindingContextManager? keyContextManager = null)
    {
        // 独立构造（测试/预览）时回退到默认绑定，行为与全局实例一致。
        _keyResolver = keyResolver ?? new KeybindingResolver();
        if (keyResolver is null)
            _keyResolver.SetBindings([.. KeybindingDefaults.GetDefaultParsedBindings()]);
        _keyContextManager = keyContextManager ?? new KeybindingContextManager();

        Width = Dim.Fill();
        Height = Dim.Fill();
        CanFocus = true;
    }

    /// <summary>当前滚动偏移（测试断言用）。</summary>
    public int ScrollOffset => _scrollOffset;

    /// <summary>Simulates a KeyDown reaching this view (test hook, mirrors OnKeyDown).</summary>
    // 仅单元测试使用：生产代码当前无调用方（测试接缝）。
    internal bool DispatchKey(Key kb) => OnKeyDown(kb);

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

        // Diff 上下文在解析时并入活跃集合：DiffView 聚焦期间才消费 diff:*，
        // 无 push/pop 生命周期可泄漏。未匹配的按键不处理，交回基础滚动逻辑。
        var contexts = new HashSet<string>(_keyContextManager.ActiveContexts, StringComparer.Ordinal)
        {
            KeybindingDefaults.ContextDiff,
        };
        switch (TuiKeyAdapter.ResolveAction(kb, _keyResolver, contexts))
        {
            case KeybindingDefaults.ActionDiffScrollUp: Scroll(-1); return true;
            case KeybindingDefaults.ActionDiffScrollDown: Scroll(1); return true;
            case KeybindingDefaults.ActionDiffPageUp: Scroll(-pageSize); return true;
            case KeybindingDefaults.ActionDiffPageDown: Scroll(pageSize); return true;
            case KeybindingDefaults.ActionDiffTop:
                _scrollOffset = 0;
                SetNeedsDraw();
                return true;
            case KeybindingDefaults.ActionDiffBottom:
                _scrollOffset = Math.Max(0, _lines.Count - vp.Height);
                SetNeedsDraw();
                return true;
        }

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


