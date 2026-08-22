namespace OneCode.App.Tui;

/// <summary>
/// Conversation line store + scrolling + input handling.
/// Rendering lives in <see cref="MessageListView.Rendering.cs"/>, search in
/// <see cref="MessageListView.Search.cs"/>, expandable blocks in
/// <see cref="MessageListView.Expansion.cs"/>.
/// </summary>
public sealed partial class MessageListView : View
{
    private readonly List<LineEntry> _lines = new();
    private readonly OneCode.Core.IO.IClipboardService? _clipboard;
    private readonly ScrollState _scroll;
    private int _toolDetailLayoutWidth;
    private int _streamingPreviewStart = -1;

    public int TotalLines => _lines.Count;
    public IReadOnlyList<string> RenderedLines => _lines.Select(static l => l.Text).ToArray();
    public int ScrollOffset => _scroll.ScrollOffset;

    /// <summary>
    /// Live streaming preview size, including user-expanded tool/thinking details.
    /// Count-based <c>ReplaceLastLines</c> cannot see those extra rows, so callers
    /// that insert above the preview must use this value.
    /// </summary>
    public int StreamingPreviewLineCount
        => _streamingPreviewStart < 0 ? 0 : Math.Max(0, _lines.Count - _streamingPreviewStart);

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
        _tailRegionStart = -1;
        _streamingPreviewStart = -1;
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
        var inserted = 0;
        foreach (var l in lines)
        {
            _lines.Insert(insertIndex++, new LineEntry(l.FullText, l.Color, l.Segments, l.Bg, l.Tag));
            inserted++;
        }
        if (_streamingPreviewStart >= 0 && insertIndex - inserted <= _streamingPreviewStart)
            _streamingPreviewStart += inserted;
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
        ShiftStreamingPreviewStart(index, -actual);
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

        var inserted = 0;
        foreach (var line in lines)
        {
            _lines.Insert(index++, new LineEntry(line.FullText, line.Color, line.Segments, line.Bg, line.Tag));
            inserted++;
        }
        ShiftStreamingPreviewStart(index - inserted, inserted - actual);

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
            {
                var removeAt = _lines.Count - actualRemove;
                _lines.RemoveRange(removeAt, actualRemove);
                ShiftStreamingPreviewStart(removeAt, -actualRemove);
            }
        }

        if (addLines is not null)
            _lines.AddRange(addLines.Select(l => new LineEntry(l.FullText, l.Color, l.Segments, l.Bg, l.Tag)));

        _scroll.RequestScrollToBottomIfAutoScroll();
        SetNeedsDraw();
    }

    /// <summary>
    /// Marks the current end of the transcript as the streaming preview start.
    /// Subsequent <see cref="ReplaceStreamingPreview"/> calls replace that window,
    /// including any detail rows the user expanded inside it.
    /// </summary>
    public void BeginStreamingPreview()
    {
        if (_streamingPreviewStart < 0)
            _streamingPreviewStart = _lines.Count;
    }

    /// <summary>Seals the current preview as committed history.</summary>
    public void EndStreamingPreview() => _streamingPreviewStart = -1;

    /// <summary>
    /// Replaces the live streaming preview window and restores tool/thinking
    /// expansion the user already opened so a thinking delta cannot collapse it.
    /// </summary>
    /// <remarks>
    /// An active tail region (inline selector / wizard) can be appended while the
    /// run is finalizing — the plan-approval selector pops exactly then, before
    /// EndStreaming commits the preview. The preview window extends to the list end,
    /// so committing it would swallow the selector lines; they are detached and
    /// re-appended so the interaction survives the final commit.
    /// </remarks>
    public void ReplaceStreamingPreview(IEnumerable<FormattedLine>? addLines)
    {
        if (_streamingPreviewStart < 0)
            _streamingPreviewStart = _lines.Count;

        var start = Math.Min(_streamingPreviewStart, _lines.Count);
        // Detach the tail region when it sits inside the preview window.
        // A tail region above the preview start is left untouched (not our window).
        List<LineEntry>? tailLines = null;
        if (_tailRegionStart is >= 0 and var tailStart
            && tailStart >= start && tailStart <= _lines.Count)
        {
            tailLines = _lines.GetRange(tailStart, _lines.Count - tailStart);
            _lines.RemoveRange(tailStart, _lines.Count - tailStart);
        }

        var toolSnapshots = new List<(string Name, string? Args, bool IsExpanded)>();
        bool? thinkingExpanded = null;
        for (var i = start; i < _lines.Count; i++)
        {
            if (_lines[i].Tag is ToolLineTag tool)
                toolSnapshots.Add((tool.Name, tool.Args, tool.IsExpanded));
            else if (_lines[i].Tag is ThinkingLineTag thinking && thinkingExpanded is null)
                thinkingExpanded = thinking.IsExpanded;
        }

        if (start < _lines.Count)
            _lines.RemoveRange(start, _lines.Count - start);

        if (addLines is not null)
        {
            foreach (var line in addLines)
                _lines.Add(new LineEntry(line.FullText, line.Color, line.Segments, line.Bg, line.Tag));
        }

        RestorePreviewExpansions(start, toolSnapshots, thinkingExpanded);

        if (tailLines is not null)
        {
            _tailRegionStart = _lines.Count;
            _lines.AddRange(tailLines);
        }

        _scroll.RequestScrollToBottomIfAutoScroll();
        SetNeedsDraw();
    }

    internal bool TryToggleExpansionAt(int lineIdx)
    {
        if (lineIdx < 0 || lineIdx >= _lines.Count)
            return false;
        switch (_lines[lineIdx].Tag)
        {
            case ToolLineTag tool:
                ToggleToolExpansion(lineIdx, tool);
                return true;
            case ThinkingLineTag thinking:
                ToggleThinkingExpansion(lineIdx, thinking);
                return true;
            case ErrorLineTag error:
                ToggleErrorExpansion(lineIdx, error);
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// 用新宽度的整体重渲结果替换全部已提交行。宽度变化（侧边栏拖拽/终端
    /// resize）后旧行的换行不再匹配视口，必须按新宽度重渲——但行内交互
    /// 状态不属于可重渲内容，需要先快照再恢复：
    /// 尾部交互区域（内联选择器/向导）原样回贴、流式预览窗口由调用方按新
    /// 宽度重建、工具/思考展开状态按出现顺序重放、滚动位置按原偏移钳制。
    /// </summary>
    internal void RerenderAll(
        Func<IReadOnlyList<FormattedLine>> rebuildCommitted,
        IReadOnlyList<FormattedLine>? previewLines)
    {
        // —— 快照：展开状态与尾部区域必须跨重建存活 ——
        var autoScroll = _scroll.AutoScroll;
        var previousOffset = _scroll.ScrollOffset;
        var tailLines = _tailRegionStart is >= 0 and var tailStart
            ? _lines.GetRange(tailStart, _lines.Count - tailStart)
            : null;
        var toolStates = new List<(string Name, string? Args, bool IsExpanded)>();
        var thinkingStates = new List<(string Content, bool IsExpanded)>();
        foreach (var entry in _lines)
        {
            switch (entry.Tag)
            {
                case ToolLineTag tool:
                    toolStates.Add((tool.Name, tool.Args, tool.IsExpanded));
                    break;
                case ThinkingLineTag thinking:
                    thinkingStates.Add((thinking.Content, thinking.IsExpanded));
                    break;
            }
        }

        // —— 重建：已提交行 → 流式预览窗口 → 尾部交互区域 ——
        _lines.Clear();
        _tailRegionStart = -1;
        _streamingPreviewStart = -1;
        foreach (var line in rebuildCommitted())
            _lines.Add(new LineEntry(line.FullText, line.Color, line.Segments, line.Bg, line.Tag));

        if (previewLines is not null)
        {
            _streamingPreviewStart = _lines.Count;
            foreach (var line in previewLines)
                _lines.Add(new LineEntry(line.FullText, line.Color, line.Segments, line.Bg, line.Tag));
        }

        if (tailLines is not null)
        {
            _tailRegionStart = _lines.Count;
            _lines.AddRange(tailLines);
        }

        ReapplyExpansionStates(toolStates, thinkingStates);

        // —— 滚动恢复：跟随底部则贴底，否则钳制原偏移 ——
        if (autoScroll)
            _scroll.RequestScrollToBottomIfAutoScroll();
        else
            _scroll.RestoreOffset(previousOffset);

        // 行索引整体变化，搜索高亮随之失效。
        SetSearchHighlight(null, null);
        SetNeedsDraw();
    }

    /// <summary>
    /// 按出现顺序将快照的展开状态重放到重渲后的行上。重渲会把工具行还原为
    /// 折叠态（渲染器默认），快照里用户已展开的行需要重新展开——展开细节行
    /// 由 tag 源数据按当前宽度重建（<see cref="ToggleToolExpansion"/>）。
    /// </summary>
    private void ReapplyExpansionStates(
        IReadOnlyList<(string Name, string? Args, bool IsExpanded)> toolStates,
        IReadOnlyList<(string Content, bool IsExpanded)> thinkingStates)
    {
        var toolIdx = 0;
        var thinkingIdx = 0;
        for (var i = 0; i < _lines.Count; i++)
        {
            switch (_lines[i].Tag)
            {
                case ToolLineTag tool:
                    if (toolIdx < toolStates.Count)
                    {
                        var want = toolStates[toolIdx++];
                        if (want.IsExpanded && !tool.IsExpanded
                            && string.Equals(want.Name, tool.Name, StringComparison.Ordinal))
                        {
                            ToggleToolExpansion(i, tool);
                        }
                    }
                    i += CountToolDetailLinesAfter(i);
                    break;
                case ThinkingLineTag thinking:
                    if (thinkingIdx < thinkingStates.Count)
                    {
                        var want = thinkingStates[thinkingIdx++];
                        if (want.IsExpanded && !thinking.IsExpanded
                            && string.Equals(want.Content, thinking.Content, StringComparison.Ordinal))
                        {
                            InsertThinkingDetails(i, thinking with { IsExpanded = true });
                        }
                        else if (!want.IsExpanded && thinking.IsExpanded)
                        {
                            ToggleThinkingExpansion(i, thinking);
                        }
                    }
                    i += CountThinkingDetailLinesAfter(i);
                    break;
            }
        }
    }

    private void ShiftStreamingPreviewStart(int changeIndex, int delta)
    {
        if (_streamingPreviewStart < 0 || delta == 0)
            return;
        if (changeIndex < _streamingPreviewStart)
            _streamingPreviewStart = Math.Max(changeIndex, _streamingPreviewStart + delta);
    }

    public void ScrollToBottom() => _scroll.ScrollToBottom();

    // —— 尾部交互区域 ——
    // 内联选择器 / 提问向导的行整体挂在对话尾部，可整体替换或移除。
    // 起始行号由 MessageListView 簿记，调用方不再维护行数（D2）。
    private int _tailRegionStart = -1;

    /// <summary>开始一个尾部交互区域，后续 <see cref="ReplaceTailRegion"/> 整体替换这些行。</summary>
    public void BeginTailRegion(IEnumerable<FormattedLine> lines)
    {
        EndTailRegion();
        _tailRegionStart = _lines.Count;
        AppendLines(lines);
    }

    /// <summary>整体替换当前尾部交互区域的内容。</summary>
    public void ReplaceTailRegion(IEnumerable<FormattedLine> lines)
    {
        if (_tailRegionStart < 0) return;
        ReplaceRange(_tailRegionStart, _lines.Count - _tailRegionStart, lines);
    }

    /// <summary>移除当前尾部交互区域。</summary>
    public void EndTailRegion()
    {
        if (_tailRegionStart < 0) return;
        var count = _lines.Count - _tailRegionStart;
        if (count > 0)
            _lines.RemoveRange(_tailRegionStart, count);
        _tailRegionStart = -1;
        _scroll.RequestScrollToBottomIfAutoScroll();
        SetNeedsDraw();
    }

    public void ScrollUp(int lines = 3) => _scroll.ScrollUp(lines);

    public void ScrollDown(int lines = 3) => _scroll.ScrollDown(lines);

    public void PageUp() => _scroll.PageUp();

    public void PageDown() => _scroll.PageDown();

    /// <summary>滚动到指定行索引（居中显示）。</summary>
    public void ScrollToLine(int lineIdx) => _scroll.ScrollToLine(lineIdx);

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

        // Tool line click-to-expand/collapse
        if (mouse.Flags.HasFlag(MouseFlags.LeftButtonClicked))
        {
            var clickedLineIdx = mouse.Position.Value.Y + _scroll.ScrollOffset;
            if (clickedLineIdx >= 0 && clickedLineIdx < _lines.Count)
            {
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
}
