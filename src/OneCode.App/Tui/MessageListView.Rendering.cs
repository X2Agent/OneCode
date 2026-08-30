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

            // Transcript 导航行：整行提亮背景并加 Accent 游标条，与搜索高亮互斥时
            // 导航优先（导航行同时被搜索命中也只呈现导航态，避免双重高亮混淆）。
            var isNavRow = lineIdx == _navHighlightLine;
            if (isNavRow)
                lineBg = TuiPalette.BgActive;

            // Always fill the full row with the background first.
            // This clears stale characters from previous frames regardless of
            // which content path (segments/plain) runs below.
            SetAttribute(new Attribute(entry.Color, lineBg));
            AddStr(new string(' ', viewport.Width));

            // 导航行在行首绘制 Accent 游标条，内容整体右移一列。
            var rowLeft = leftPad;
            if (isNavRow)
            {
                Move(0, i);
                SetAttribute(new Attribute(TuiPalette.Accent, lineBg));
                AddStr(TuiGlyphs.BlockFull);
                rowLeft += 1;
            }

            Move(rowLeft, i);

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
                    // 按显示宽度裁剪：字符数裁剪遇 CJK 会溢出 remaining 列。
                    text = TextWidthHelper.ClipByWidth(text, remaining);
                    SetAttribute(new Attribute(fg, isMatch ? highlightBg : segBg));
                    AddStr(text);
                    remaining -= TextWidthHelper.GetDisplayWidth(text);
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
                    var clipped = TextWidthHelper.ClipByWidth(seg.Text, remaining);
                    AddStr(clipped);
                    remaining -= TextWidthHelper.GetDisplayWidth(clipped);
                }
                // remaining chars already cleared by the initial fill above.
            }
            else
            {
                SetAttribute(new Attribute(entry.Color, lineBg));
                // 绘制期硬裁剪不能用 TruncateByWidth：它为省略号预留 1 列，会把
                // 恰好满宽的行尾字符替换成 "…"（换行后的工具 JSON 行每行必然
                // 触顶，看起来像右侧显示不全）。此处裁剪是视口硬边界，无隐藏内容。
                AddStr(TextWidthHelper.ClipByWidth(entry.Text, contentWidth));
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
