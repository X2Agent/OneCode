namespace OneCode.App.Tui;

/// <summary>
/// Click-to-expand/collapse for tool / thinking / error blocks in
/// <see cref="MessageListView"/>, plus width-driven reflow of expanded details.
/// </summary>
public sealed partial class MessageListView
{
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

    private int CountToolDetailLinesAfter(int lineIdx)
    {
        var count = 0;
        for (var i = lineIdx + 1; i < _lines.Count && _lines[i].Tag is ToolDetailLineTag; i++)
            count++;
        return count;
    }

    private int CountThinkingDetailLinesAfter(int lineIdx)
    {
        var count = 0;
        for (var i = lineIdx + 1; i < _lines.Count && _lines[i].Tag is ThinkingDetailLineTag; i++)
            count++;
        return count;
    }

    /// <summary>
    /// Re-applies expand/collapse choices from the previous preview window onto
    /// the rebuilt streaming lines. Incoming status lines are always collapsed.
    /// </summary>
    private void RestorePreviewExpansions(
        int previewStart,
        IReadOnlyList<(string Name, string? Args, bool IsExpanded)> toolSnapshots,
        bool? thinkingExpanded)
    {
        var toolIndex = 0;
        for (var i = previewStart; i < _lines.Count; i++)
        {
            if (_lines[i].Tag is ToolLineTag tool)
            {
                var shouldExpand = toolIndex < toolSnapshots.Count
                    && string.Equals(toolSnapshots[toolIndex].Name, tool.Name, StringComparison.Ordinal)
                    && toolSnapshots[toolIndex].IsExpanded;
                toolIndex++;
                if (shouldExpand && !tool.IsExpanded)
                {
                    ToggleToolExpansion(i, tool);
                    i += CountToolDetailLinesAfter(i);
                }
                else
                {
                    i += CountToolDetailLinesAfter(i);
                }
                continue;
            }

            if (_lines[i].Tag is not ThinkingLineTag thinking)
                continue;

            var expand = thinkingExpanded ?? thinking.IsExpanded;
            if (expand && CountThinkingDetailLinesAfter(i) == 0)
                InsertThinkingDetails(i, thinking with { IsExpanded = true });
            else if (!expand && thinking.IsExpanded)
                ToggleThinkingExpansion(i, thinking);
            else if (!expand && CountThinkingDetailLinesAfter(i) > 0)
                ToggleThinkingExpansion(i, thinking with { IsExpanded = true });

            i += CountThinkingDetailLinesAfter(i);
        }
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
            var removeCount = CountThinkingDetailLinesAfter(lineIdx);
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
            InsertThinkingDetails(lineIdx, tag with { IsExpanded = true });
        }

        _scroll.RequestScrollToBottomIfAutoScroll();
        SetNeedsDraw();
    }

    private void InsertThinkingDetails(int lineIdx, ThinkingLineTag expanded)
    {
        var entry = _lines[lineIdx];
        _lines[lineIdx] = new LineEntry(
            entry.Text,
            entry.Color,
            MessageRenderer.ReplaceTriangleSymbol(entry.Segments, collapsed: false),
            entry.Bg,
            expanded);

        var existing = CountThinkingDetailLinesAfter(lineIdx);
        if (existing > 0)
            _lines.RemoveRange(lineIdx + 1, existing);

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
