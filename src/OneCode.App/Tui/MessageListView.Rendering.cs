using System.Text.RegularExpressions;

namespace OneCode.App.Tui;

/// <summary>
/// Draw pass for <see cref="MessageListView"/>: visible line rendering
/// (segments / search highlight) and the scroll indicator.
/// </summary>
public sealed partial class MessageListView
{
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
}
