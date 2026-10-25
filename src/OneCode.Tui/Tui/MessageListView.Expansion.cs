namespace OneCode.Tui;

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
        // 调用方需传入预留滚动条列后的宽度（与绘制期 contentWidth 对齐），
        // 避免展开详情比绘制可用宽度宽 1 列而被绘制层截断。
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

            _toolDetailLayoutWidth = ContentWidth;
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

        const int MaxStreamingThinkingPreviewLines = 4; // Cap live reasoning preview height so long thoughts don't push the viewport off-screen

        var maxContentWidth = Math.Max(20, ContentWidth - ConversationRenderer.ContentIndent - 2);
        List<string> wrappedLines = [];
        foreach (var line in expanded.Content
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n'))
        {
            foreach (var w in TextWidthHelper.WordWrapByWidth(line, maxContentWidth))
                wrappedLines.Add(w);
        }

        var isLiveStreamingPreview = _streamingPreviewStart >= 0 && lineIdx >= _streamingPreviewStart;
        List<LineEntry> details = [];
        if (isLiveStreamingPreview && wrappedLines.Count > MaxStreamingThinkingPreviewLines)
        {
            var hiddenCount = wrappedLines.Count - MaxStreamingThinkingPreviewLines;
            details.Add(new LineEntry(
                $"{ConversationRenderer.Indent}  \u2026 (+{hiddenCount} lines)",
                TuiPalette.FgMuted,
                null,
                null,
                new ThinkingDetailLineTag()));

            for (var i = wrappedLines.Count - MaxStreamingThinkingPreviewLines; i < wrappedLines.Count; i++)
            {
                details.Add(new LineEntry(
                    $"{ConversationRenderer.Indent}  {wrappedLines[i]}",
                    TuiPalette.FgSecondary,
                    null,
                    null,
                    new ThinkingDetailLineTag()));
            }
        }
        else
        {
            foreach (var w in wrappedLines)
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

            var maxContentWidth = Math.Max(20, ContentWidth - ConversationRenderer.ContentIndent - 2);
            List<LineEntry> details = [];
            var contentLines = expanded.Content
                .Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
            // Skip the first line — already shown (possibly truncated) in the summary.
            // If it was truncated, still show the full first line in the detail block.
            // 判定与 RenderErrorBlock 一致：按显示宽度 + 共享预算公式（旧实现按
            // 字符数，CJK 首行会被误判为"已完整展示"而丢失详情）。
            var startIdx = 0;
            if (contentLines.Length > 0
                && TextWidthHelper.GetDisplayWidth(contentLines[0]) <= ConversationRenderer.ErrorSummaryBudget(ContentWidth))
            {
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

    private object? _lastKeyboardToggledTag;
    private int _lastKeyboardToggledLineIdx = -1;

    /// <summary>最近一次通过键盘（Ctrl+E）切换展开态的摘要行索引（测试断言与诊断用）。</summary>
    internal int LastKeyboardToggledLineIndex => _lastKeyboardToggledLineIdx;

    /// <summary>
    /// 视口感知的多块循环展开/折叠（Ctrl+E）：
    /// - 处于底部自动跟随时：在末尾视口（或全量会话）中逆序循环切换可折叠块；
    /// - 向上翻页浏览历史（!AutoScroll）时：优先在当前可见视口 [ScrollOffset, ScrollOffset + visibleLines)
    ///   内逆序循环切换；若当前视口无折叠块则向上回退查找 [0, ScrollOffset)，并调用 ScrollToLine 保证目标可见。
    /// </summary>
    public bool ToggleLastDetails()
    {
        if (_lines.Count == 0)
            return false;

        List<int> candidates;
        var visibleLines = Viewport.Height > 0 ? Viewport.Height : 10;

        if (!_scroll.AutoScroll)
        {
            var viewStart = Math.Clamp(_scroll.ScrollOffset, 0, _lines.Count);
            var viewEnd = Math.Min(_lines.Count, viewStart + visibleLines);
            candidates = CollectCollapsibleSummaryLines(viewStart, viewEnd);
            if (candidates.Count == 0 && viewStart > 0)
                candidates = CollectCollapsibleSummaryLines(0, viewStart);
            if (candidates.Count == 0)
                candidates = CollectCollapsibleSummaryLines(0, _lines.Count);
        }
        else
        {
            if (Viewport.Height > 0)
            {
                var tailStart = Math.Max(0, _lines.Count - Viewport.Height);
                candidates = CollectCollapsibleSummaryLines(tailStart, _lines.Count);
                if (candidates.Count == 0)
                    candidates = CollectCollapsibleSummaryLines(0, _lines.Count);
            }
            else
            {
                candidates = CollectCollapsibleSummaryLines(0, _lines.Count);
            }
        }

        if (candidates.Count == 0)
            return false;

        var targetIdx = candidates[0];
        if (_lastKeyboardToggledTag is not null)
        {
            var prevPos = candidates.FindIndex(idx => ReferenceEquals(_lines[idx].Tag, _lastKeyboardToggledTag));
            if (prevPos >= 0)
                targetIdx = candidates[(prevPos + 1) % candidates.Count];
        }

        switch (_lines[targetIdx].Tag)
        {
            case ToolLineTag toolTag:
                ToggleToolExpansion(targetIdx, toolTag);
                break;
            case ThinkingLineTag thinkingTag:
                ToggleThinkingExpansion(targetIdx, thinkingTag);
                break;
            case ErrorLineTag errorTag:
                ToggleErrorExpansion(targetIdx, errorTag);
                break;
            default:
                return false;
        }

        _lastKeyboardToggledLineIdx = targetIdx;
        _lastKeyboardToggledTag = _lines[targetIdx].Tag;

        if (!_scroll.AutoScroll && (targetIdx < _scroll.ScrollOffset || targetIdx >= _scroll.ScrollOffset + visibleLines))
        {
            _scroll.ScrollToLine(targetIdx);
            SetNeedsDraw();
        }

        return true;
    }

    private List<int> CollectCollapsibleSummaryLines(int startInclusive, int endExclusive)
    {
        List<int> indices = [];
        var start = Math.Max(0, startInclusive);
        var end = Math.Min(_lines.Count, endExclusive);
        for (var i = end - 1; i >= start; i--)
        {
            switch (_lines[i].Tag)
            {
                case ToolLineTag:
                case ThinkingLineTag:
                    indices.Add(i);
                    break;
                case ErrorLineTag errorTag:
                    // Error 展开详情行与首行摘要共享相同的 ErrorLineTag.Content；
                    // 跳过详情行，定位到该错误块的首行摘要。
                    while (i > 0 && _lines[i - 1].Tag is ErrorLineTag prevErr && prevErr.Content == errorTag.Content)
                        i--;
                    if (i >= start)
                        indices.Add(i);
                    break;
            }
        }
        return indices;
    }
}
