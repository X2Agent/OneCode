using System.Text.RegularExpressions;

namespace OneCode.App.Tui;

public sealed class MessageListView : View
{
    private readonly List<LineEntry> _lines = new();
    private readonly OneCode.Core.IO.IClipboardService? _clipboard;
    private readonly ScrollState _scroll;
    private int _toolDetailLayoutWidth;

    public int TotalLines => _lines.Count;
    public IReadOnlyList<string> RenderedLines => _lines.Select(static l => l.Text).ToArray();
    public int ScrollOffset => _scroll.ScrollOffset;

    public MessageListView(OneCode.Core.IO.IClipboardService? clipboard = null)
    {
        _clipboard = clipboard;
        _scroll = new ScrollState(
            () => Viewport.Height,
            () => _lines.Count,
            onNeedsDraw: () => SetNeedsDraw());
        CanFocus = false;
        TabStop = TabBehavior.NoStop;
        Width = Dim.Fill();
        Height = Dim.Fill();
    }

    public void Clear()
    {
        _lines.Clear();
        _toolDetailLayoutWidth = 0;
        _scroll.Reset();
        SetNeedsDraw();
    }

    public void AppendLine(string text, Color color)
    {
        _lines.Add(new LineEntry(text, color, null));
        _scroll.RequestScrollToBottomIfAutoScroll();
        SetNeedsDraw();
    }

    public void AppendLines(IEnumerable<FormattedLine> lines)
    {
        foreach (var l in lines)
            _lines.Add(new LineEntry(l.FullText, l.Color, l.Segments, l.Bg, l.Tag));
        _scroll.RequestScrollToBottomIfAutoScroll();
        SetNeedsDraw();
    }

    /// <summary>
    /// Inserts lines before the last <paramref name="tailCount"/> lines (the streaming preview),
    /// preserving both the new lines and the existing streaming preview. Used when a mode banner
    /// arrives during streaming — inserting it before the preview prevents ReplaceLastLines from
    /// deleting the banner on the next streaming update.
    /// </summary>
    public void InsertBeforeLast(int tailCount, IEnumerable<FormattedLine> lines)
    {
        var insertIndex = _lines.Count - Math.Min(tailCount, _lines.Count);
        if (insertIndex < 0) insertIndex = 0;
        foreach (var l in lines)
            _lines.Insert(insertIndex++, new LineEntry(l.FullText, l.Color, l.Segments, l.Bg, l.Tag));
        _scroll.RequestScrollToBottomIfAutoScroll();
        SetNeedsDraw();
    }

    /// <summary>
    /// Removes <paramref name="count"/> lines starting at <paramref name="index"/>.
    /// </summary>
    public void RemoveRange(int index, int count)
    {
        if (count <= 0 || index < 0 || index >= _lines.Count) return;
        var actual = Math.Min(count, _lines.Count - index);
        if (actual <= 0) return;
        _lines.RemoveRange(index, actual);
        _scroll.RequestScrollToBottomIfAutoScroll();
        SetNeedsDraw();
    }

    /// <summary>Replaces a stable range without disturbing content before or after it.</summary>
    public void ReplaceRange(int index, int count, IEnumerable<FormattedLine> lines)
    {
        if (index < 0 || index > _lines.Count) return;
        var actual = Math.Min(Math.Max(0, count), _lines.Count - index);
        if (actual > 0)
            _lines.RemoveRange(index, actual);

        foreach (var line in lines)
            _lines.Insert(index++, new LineEntry(line.FullText, line.Color, line.Segments, line.Bg, line.Tag));

        _scroll.RequestScrollToBottomIfAutoScroll();
        SetNeedsDraw();
    }

    public void UpdateLastLines(IEnumerable<FormattedLine> lines)
    {
        var updates = lines.ToList();
        if (updates.Count == 0) return;

        if (_lines.Count >= updates.Count)
        {
            var startIdx = _lines.Count - updates.Count;
            for (var i = 0; i < updates.Count; i++)
                _lines[startIdx + i] = new LineEntry(updates[i].FullText, updates[i].Color, updates[i].Segments, updates[i].Bg, updates[i].Tag);
        }
        else
        {
            _lines.Clear();
            _lines.AddRange(updates.Select(l => new LineEntry(l.FullText, l.Color, l.Segments, l.Bg, l.Tag)));
        }

        _scroll.RequestScrollToBottomIfAutoScroll();
        SetNeedsDraw();
    }

    /// <summary>
    /// Removes the last <paramref name="removeCount"/> lines (the current streaming preview),
    /// then appends <paramref name="addLines"/>. Used to replace in-flight streaming lines
    /// with updated content without corrupting history lines above them.
    /// </summary>
    public void ReplaceLastLines(int removeCount, IEnumerable<FormattedLine>? addLines)
    {
        if (removeCount > 0)
        {
            var actualRemove = Math.Min(removeCount, _lines.Count);
            if (actualRemove > 0)
                _lines.RemoveRange(_lines.Count - actualRemove, actualRemove);
        }

        if (addLines is not null)
            _lines.AddRange(addLines.Select(l => new LineEntry(l.FullText, l.Color, l.Segments, l.Bg, l.Tag)));

        _scroll.RequestScrollToBottomIfAutoScroll();
        SetNeedsDraw();
    }

    public void ScrollToBottom() => _scroll.ScrollToBottom();

    /// <summary>
    /// Rebuilds all expanded tool detail blocks using the current content width.
    /// A redraw alone is insufficient because detail lines are materialized and
    /// wrapped when expanded.
    /// </summary>
    public void ReflowExpandedToolDetails(int viewportWidth)
    {
        var contentWidth = TuiSpacing.GetContentColumnWidth(viewportWidth);
        if (contentWidth <= 0 || contentWidth == _toolDetailLayoutWidth)
            return;

        _toolDetailLayoutWidth = contentWidth;
        var changed = false;

        for (var lineIndex = 0; lineIndex < _lines.Count; lineIndex++)
        {
            if (_lines[lineIndex].Tag is not ToolLineTag { IsExpanded: true } tag)
                continue;

            var removeCount = CountToolDetailLinesAfter(lineIndex);
            if (removeCount > 0)
                _lines.RemoveRange(lineIndex + 1, removeCount);

            var details = MessageRenderer.BuildToolDetailLines(tag, contentWidth);
            _lines.InsertRange(lineIndex + 1, details);
            lineIndex += details.Count;
            changed = true;
        }

        if (!changed)
            return;

        _scroll.RequestScrollToBottomIfAutoScroll();
        SetNeedsLayout();
        SetNeedsDraw();
    }

    /// <summary>
    /// 搜索包含指定文本的行，返回所有匹配行的索引列表。
    /// </summary>
    public List<int> FindMatches(string query, StringComparison comparison = StringComparison.OrdinalIgnoreCase)
    {
        var results = new List<int>();
        if (string.IsNullOrWhiteSpace(query)) return results;
        for (var i = 0; i < _lines.Count; i++)
        {
            if (_lines[i].Text.Contains(query, comparison))
                results.Add(i);
        }
        return results;
    }

    /// <summary>
    /// 使用正则表达式搜索匹配行。
    /// <paramref name="compiled"/> 返回本次编译的正则，供 <see cref="SetSearchHighlight"/> 复用，避免二次编译。
    /// </summary>
    public List<int> FindMatchesRegex(
        string pattern, out Regex? compiled, RegexOptions options = RegexOptions.IgnoreCase)
    {
        compiled = null;
        var results = new List<int>();
        if (string.IsNullOrWhiteSpace(pattern)) return results;
        compiled = new Regex(pattern, options, TimeSpan.FromSeconds(1));
        for (var i = 0; i < _lines.Count; i++)
        {
            if (compiled.IsMatch(_lines[i].Text))
                results.Add(i);
        }
        return results;
    }

    /// <summary>滚动到指定行索引（居中显示）。</summary>
    public void ScrollToLine(int lineIdx) => _scroll.ScrollToLine(lineIdx);

    private string? _highlightQuery;
    private bool _highlightIsRegex;
    private Regex? _highlightRegex;
    private HashSet<int>? _highlightedLineIndices;

    /// <summary>
    /// 设置搜索高亮：在指定行中高亮匹配的关键词。
    /// 传 null / 空列表清除高亮。
    /// 优先使用 <paramref name="compiledRegex"/>（与搜索侧共用同一实例）；
    /// 若仅设 <paramref name="isRegex"/> 则就地编译，失败时跳过行内高亮（不抛、也不回退为字面量匹配）。
    /// </summary>
    public void SetSearchHighlight(
        string? query,
        IReadOnlyList<int>? matchedLineIndices,
        bool isRegex = false,
        Regex? compiledRegex = null)
    {
        _highlightedLineIndices = matchedLineIndices is { Count: > 0 }
            ? new HashSet<int>(matchedLineIndices)
            : null;

        if (compiledRegex is not null)
        {
            _highlightQuery = query;
            _highlightRegex = compiledRegex;
            _highlightIsRegex = true;
        }
        else if (isRegex && !string.IsNullOrEmpty(query))
        {
            try
            {
                _highlightRegex = new Regex(query, RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
                _highlightQuery = query;
                _highlightIsRegex = true;
            }
            catch (RegexParseException)
            {
                // 无效 pattern：保留匹配行索引用于滚动，但不做行内高亮（避免字面量 IndexOf 误匹配）
                _highlightRegex = null;
                _highlightQuery = null;
                _highlightIsRegex = false;
            }
        }
        else
        {
            _highlightQuery = query;
            _highlightRegex = null;
            _highlightIsRegex = false;
        }

        SetNeedsDraw();
    }

    public void ScrollUp(int lines = 3) => _scroll.ScrollUp(lines);

    public void ScrollDown(int lines = 3) => _scroll.ScrollDown(lines);

    public void PageUp() => _scroll.PageUp();

    public void PageDown() => _scroll.PageDown();

    protected override bool OnMouseEvent(Mouse mouse)
    {
        if (mouse.Flags.HasFlag(MouseFlags.WheeledUp))
        {
            ScrollUp();
            return true;
        }

        if (mouse.Flags.HasFlag(MouseFlags.WheeledDown))
        {
            ScrollDown();
            return true;
        }

        // Tool line click-to-expand/collapse OR code-block copy icon click
        if (mouse.Flags.HasFlag(MouseFlags.LeftButtonClicked))
        {
            var clickedLineIdx = mouse.Position.Value.Y + _scroll.ScrollOffset;
            if (clickedLineIdx >= 0 && clickedLineIdx < _lines.Count)
            {
                // Code block copy: clicking the header line of a fenced code
                // block copies the code content to the clipboard.
                if (_lines[clickedLineIdx].Tag is CodeBlockCopyTag codeTag)
                {
                    if (_clipboard is not null)
                        _ = _clipboard.TryCopyTextAsync(codeTag.Code);
                    return true;
                }
                if (_lines[clickedLineIdx].Tag is ToolLineTag tag)
                {
                    ToggleToolExpansion(clickedLineIdx, tag);
                    return true;
                }
                if (_lines[clickedLineIdx].Tag is ThinkingLineTag thinkingTag)
                {
                    ToggleThinkingExpansion(clickedLineIdx, thinkingTag);
                    return true;
                }
                if (_lines[clickedLineIdx].Tag is ErrorLineTag errorTag)
                {
                    ToggleErrorExpansion(clickedLineIdx, errorTag);
                    return true;
                }
            }
        }

        return base.OnMouseEvent(mouse);
    }

    /// <summary>
    /// Clamps a mouse Y coordinate to the valid viewport range [0, viewportHeight-1].
    /// Prevents ArgumentOutOfRangeException when the mouse is slightly outside the view.
    /// </summary>
    private int ClampMouseY(int y)
    {
        var max = Math.Max(0, Viewport.Height - 1);
        return Math.Clamp(y, 0, max);
    }

    protected override bool OnKeyDown(Key kb)
    {
        switch (kb)
        {
            case var k when k == Key.CursorUp: ScrollUp(); return true;
            case var k when k == Key.CursorDown: ScrollDown(); return true;
            case var k when k == Key.PageUp: PageUp(); return true;
            case var k when k == Key.PageDown: PageDown(); return true;
            case var k when k == Key.Home:
                _scroll.ScrollToTop();
                return true;
            case var k when k == Key.End:
                _scroll.ScrollToEnd();
                return true;
            default:
                return base.OnKeyDown(kb);
        }
    }

    protected override bool OnDrawingContent(DrawContext? context)
    {
        if (_scroll.NeedsScrollToBottom)
            _scroll.ScrollToBottom();

        base.OnDrawingContent(context);

        if (_lines.Count == 0) return false;

        var viewport = Viewport;
        var showScrollbar = _lines.Count > viewport.Height;
        // Reserve the rightmost column for the scrollbar when needed; content
        // otherwise fills the available width (no artificial center column).
        var availableWidth = showScrollbar ? Math.Max(0, viewport.Width - 1) : viewport.Width;
        var contentWidth = TuiSpacing.GetContentColumnWidth(availableWidth);
        var leftPad = 0;
        var visibleLines = Math.Min(viewport.Height, _lines.Count - _scroll.ScrollOffset);

        for (var i = 0; i < visibleLines; i++)
        {
            var lineIdx = _scroll.ScrollOffset + i;
            if (lineIdx >= _lines.Count) break;

            var entry = _lines[lineIdx];
            Move(0, i);

            var lineBg = entry.Bg ?? TuiPalette.BgPrimary;

            // Always fill the full row with the background first.
            // This clears stale characters from previous frames regardless of
            // which content path (segments/plain) runs below.
            SetAttribute(new Attribute(entry.Color, lineBg));
            AddStr(new string(' ', viewport.Width));
            Move(leftPad, i);

            // Search highlight: split the line's text around the query and render
            // matched portions with a distinct background color.
            if (_highlightedLineIndices is not null
                && _highlightedLineIndices.Contains(lineIdx)
                && !string.IsNullOrEmpty(_highlightQuery))
            {
                var remaining = contentWidth;
                var highlightBg = TuiPalette.Warning;
                var segBg = lineBg;

                void RenderHighlight(string text, Color fg, bool isMatch)
                {
                    if (remaining <= 0 || string.IsNullOrEmpty(text)) return;
                    if (text.Length > remaining)
                        text = text[..remaining];
                    SetAttribute(new Attribute(fg, isMatch ? highlightBg : segBg));
                    AddStr(text);
                    remaining -= text.Length;
                }

                void RenderText(string text, Color fg)
                {
                    if (_highlightIsRegex && _highlightRegex is not null)
                        SplitAndRenderRegex(text, _highlightRegex, fg, RenderHighlight);
                    else
                        SplitAndRender(text, _highlightQuery!, fg, RenderHighlight);
                }

                if (entry.Segments is { Count: > 0 })
                {
                    foreach (var seg in entry.Segments)
                        RenderText(seg.Text, seg.Color);
                }
                else
                {
                    RenderText(entry.Text, entry.Color);
                }
                continue;
            }

            if (entry.Segments is { Count: > 0 })
            {
                var remaining = contentWidth;
                foreach (var seg in entry.Segments)
                {
                    if (remaining <= 0) break;
                    var segBg = seg.Bg ?? lineBg;
                    SetAttribute(new Attribute(seg.Color, segBg));
                    if (seg.Text.Length <= remaining)
                    {
                        AddStr(seg.Text);
                        remaining -= seg.Text.Length;
                    }
                    else
                    {
                        AddStr(seg.Text[..remaining]);
                        remaining = 0;
                    }
                }
                // remaining chars already cleared by the initial fill above.
            }
            else
            {
                SetAttribute(new Attribute(entry.Color, lineBg));
                AddStr(MessageRenderer.TruncateVisual(entry.Text, contentWidth));
                // remaining chars already cleared by the initial fill above.
            }
        }

        if (showScrollbar)
            DrawScrollIndicator();

        return false;
    }

    /// <summary>
    /// 将文本按搜索关键词拆分为匹配/非匹配段，通过回调渲染。
    /// </summary>
    private static void SplitAndRender(string text, string query, Color fg, Action<string, Color, bool> render)
    {
        if (string.IsNullOrEmpty(text))
            return;

        var idx = 0;
        while (idx < text.Length)
        {
            var matchPos = text.AsSpan(idx).IndexOf(query.AsSpan(), StringComparison.OrdinalIgnoreCase);
            if (matchPos < 0)
            {
                render(text[idx..], fg, false);
                return;
            }

            if (matchPos > 0)
                render(text[idx..(idx + matchPos)], fg, false);

            var matchLen = query.Length;
            render(text[(idx + matchPos)..(idx + matchPos + matchLen)], fg, true);
            idx += matchPos + matchLen;
        }
    }

    /// <summary>
    /// 将文本按正则匹配拆分为匹配/非匹配段，通过回调渲染。
    /// </summary>
    private static void SplitAndRenderRegex(string text, Regex regex, Color fg, Action<string, Color, bool> render)
    {
        if (string.IsNullOrEmpty(text))
            return;

        var idx = 0;
        foreach (Match m in regex.Matches(text))
        {
            if (m.Index > idx)
                render(text[idx..m.Index], fg, false);
            render(m.Value, fg, true);
            idx = m.Index + m.Length;
        }

        if (idx < text.Length)
            render(text[idx..], fg, false);
    }

    /// <summary>
    /// 在视口右侧绘制 1 列宽的滚动位置指示器。
    /// 仅当内容行数超过视口高度时显示。拇指位置反映当前滚动偏移。
    /// </summary>
    private void DrawScrollIndicator()
    {
        var viewport = Viewport;
        var barCol = viewport.Width - 1;
        if (barCol < 0) return;

        var scrollRange = _lines.Count - viewport.Height;
        if (scrollRange <= 0) return;

        var thumbSize = Math.Max(1, viewport.Height * viewport.Height / _lines.Count);
        var thumbPos = (int)((float)_scroll.ScrollOffset / scrollRange * (viewport.Height - thumbSize));

        for (var i = 0; i < viewport.Height; i++)
        {
            Move(barCol, i);
            if (i >= thumbPos && i < thumbPos + thumbSize)
            {
                SetAttribute(new Attribute(TuiPalette.FgSecondary, TuiPalette.BgPrimary));
                AddStr(TuiGlyphs.BlockFull);
            }
            else
            {
                SetAttribute(new Attribute(TuiPalette.FgMuted, TuiPalette.BgPrimary));
                AddStr(TuiGlyphs.BlockLight);
            }
        }
    }

    private int CountToolDetailLinesAfter(int lineIdx)
    {
        var count = 0;
        for (var i = lineIdx + 1; i < _lines.Count && _lines[i].Tag is ToolDetailLineTag; i++)
            count++;
        return count;
    }

    private void ToggleToolExpansion(int lineIdx, ToolLineTag tag)
    {
        if (tag.IsExpanded)
        {
            var removeCount = CountToolDetailLinesAfter(lineIdx);
            if (removeCount > 0)
                _lines.RemoveRange(lineIdx + 1, removeCount);

            var collapsed = tag with { IsExpanded = false };
            var entry = _lines[lineIdx];
            var newSegments = MessageRenderer.ReplaceTriangleSymbol(entry.Segments, collapsed: true);
            _lines[lineIdx] = new LineEntry(entry.Text, entry.Color, newSegments, entry.Bg, collapsed);
        }
        else
        {
            var expanded = tag with { IsExpanded = true };
            var entry = _lines[lineIdx];
            var newSegments = MessageRenderer.ReplaceTriangleSymbol(entry.Segments, collapsed: false);
            _lines[lineIdx] = new LineEntry(entry.Text, entry.Color, newSegments, entry.Bg, expanded);

            _toolDetailLayoutWidth = TuiSpacing.GetContentColumnWidth(Viewport.Width);
            var detailLines = MessageRenderer.BuildToolDetailLines(expanded, _toolDetailLayoutWidth);
            _lines.InsertRange(lineIdx + 1, detailLines);
        }
        _scroll.RequestScrollToBottomIfAutoScroll();
        SetNeedsDraw();
    }

    private void ToggleThinkingExpansion(int lineIdx, ThinkingLineTag tag)
    {
        if (tag.IsExpanded)
        {
            var removeCount = 0;
            for (var i = lineIdx + 1; i < _lines.Count; i++)
            {
                if (_lines[i].Tag is ThinkingDetailLineTag)
                    removeCount++;
                else
                    break;
            }

            if (removeCount > 0)
                _lines.RemoveRange(lineIdx + 1, removeCount);

            var collapsed = tag with { IsExpanded = false };
            var entry = _lines[lineIdx];
            _lines[lineIdx] = new LineEntry(
                entry.Text,
                entry.Color,
                MessageRenderer.ReplaceTriangleSymbol(entry.Segments, collapsed: true),
                entry.Bg,
                collapsed);
        }
        else
        {
            var expanded = tag with { IsExpanded = true };
            var entry = _lines[lineIdx];
            _lines[lineIdx] = new LineEntry(
                entry.Text,
                entry.Color,
                MessageRenderer.ReplaceTriangleSymbol(entry.Segments, collapsed: false),
                entry.Bg,
                expanded);

            var maxContentWidth = Math.Max(20, TuiSpacing.GetContentColumnWidth(Viewport.Width) - ConversationRenderer.ContentIndent - 2);
            var details = new List<LineEntry>();
            foreach (var line in expanded.Content
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Split('\n'))
            {
                foreach (var w in TextWidthHelper.WordWrapByWidth(line, maxContentWidth))
                {
                    details.Add(new LineEntry(
                        $"{ConversationRenderer.Indent}  {w}",
                        TuiPalette.FgSecondary,
                        null,
                        null,
                        new ThinkingDetailLineTag()));
                }
            }
            details.Add(new LineEntry(
                "",
                TuiPalette.FgMuted,
                null,
                null,
                new ThinkingDetailLineTag()));
            _lines.InsertRange(lineIdx + 1, details);
        }

        _scroll.RequestScrollToBottomIfAutoScroll();
        SetNeedsDraw();
    }

    /// <summary>切换错误详情的展开/折叠状态。</summary>
    private void ToggleErrorExpansion(int lineIdx, ErrorLineTag tag)
    {
        if (tag.IsExpanded)
        {
            var removeCount = 0;
            for (var i = lineIdx + 1; i < _lines.Count; i++)
            {
                if (_lines[i].Tag is ErrorLineTag { IsExpanded: true } subTag && subTag.Content == tag.Content)
                    removeCount++;
                else break;
            }
            if (removeCount > 0)
                _lines.RemoveRange(lineIdx + 1, removeCount);

            var collapsed = tag with { IsExpanded = false };
            var entry = _lines[lineIdx];
            _lines[lineIdx] = new LineEntry(
                entry.Text, entry.Color,
                MessageRenderer.ReplaceTriangleSymbol(entry.Segments, collapsed: true),
                entry.Bg, collapsed);
        }
        else
        {
            var expanded = tag with { IsExpanded = true };
            var entry = _lines[lineIdx];
            _lines[lineIdx] = new LineEntry(
                entry.Text, entry.Color,
                MessageRenderer.ReplaceTriangleSymbol(entry.Segments, collapsed: false),
                entry.Bg, expanded);

            var maxContentWidth = Math.Max(20, TuiSpacing.GetContentColumnWidth(Viewport.Width) - ConversationRenderer.ContentIndent - 2);
            var details = new List<LineEntry>();
            var contentLines = expanded.Content
                .Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
            // Skip the first line — already shown (possibly truncated) in the summary.
            // If it was truncated, still show the full first line in the detail block.
            var startIdx = 0;
            if (contentLines.Length > 0)
            {
                var first = contentLines[0];
                var summaryBudget = Math.Max(20, TuiSpacing.GetContentColumnWidth(Viewport.Width) - 6);
                if (first.Length <= summaryBudget - 6)
                    startIdx = 1;
            }

            for (var li = startIdx; li < contentLines.Length; li++)
            {
                var line = contentLines[li];
                if (string.IsNullOrEmpty(line))
                {
                    details.Add(new LineEntry(
                        ConversationRenderer.Indent, TuiPalette.Error, null, null, expanded));
                    continue;
                }
                foreach (var w in TextWidthHelper.WordWrapByWidth(line, maxContentWidth))
                {
                    details.Add(new LineEntry(
                        $"{ConversationRenderer.Indent}  {w}",
                        TuiPalette.Error, null, null,
                        expanded));
                }
            }
            if (details.Count > 0)
                _lines.InsertRange(lineIdx + 1, details);
        }

        _scroll.RequestScrollToBottomIfAutoScroll();
        SetNeedsDraw();
    }
}
