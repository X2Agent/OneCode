using System.Text;

namespace OneCode.Tui;

/// <summary>
/// Manages the live streaming preview lifecycle: begin/continue/end streaming,
/// token append with debounced rebuild, and final markdown commit.
/// </summary>
public sealed partial class ChatTranscriptView
{
    private const int StreamingRebuildCadenceMs = 16; // ~60 FPS cap

    /// <summary>流式光标闪烁间隔（毫秒）。新 token 到达时光标重置为常亮，静默期闪烁指示等待。</summary>
    private const int CursorBlinkCadenceMs = 500;

    private object? _cursorBlinkTimer;
    private bool _streamingCursorVisible = true;

    public void BeginStreaming()
    {
        _stream.ResetForNewStream(new AssistantMessage(
            Id: Guid.NewGuid().ToString("N"),
            Content: new List<ContentBlock>(),
            Timestamp: DateTimeOffset.UtcNow));
        _messageView.BeginStreamingPreview();
        StartCursorBlink();

        _app.Invoke(() => ActivityChanged?.Invoke("处理中"));
    }

    /// <summary>
    /// Continues an existing streaming session for a new turn (e.g., after tool calls).
    /// Unlike <see cref="BeginStreaming"/>, this does NOT create a new assistant header,
    /// so the multi-turn response appears as a single continuous message.
    /// </summary>
    public void ContinueStreaming()
    {
        _logger.LogDebug(
            "[ContinueStreaming] statusLines={StatusLines}, lineCountInView={LineCountInView}",
            _stream.StatusLines.Count, _stream.PreviewLineCount);
        // Commit the current turn's text/tools as permanent lines before starting
        // a new turn. Thinking from this turn is finalized as a summary and
        // committed along with the other status lines — each turn's thinking
        // is independent, no cross-turn carrying.
        if (_stream.HasThinking)
            FinalizeThinkingMarker();

        if (_stream.PendingLines is { Count: > 0 } && _messageView.StreamingPreviewLineCount > 0)
        {
            // 中间工具轮（Turn 1..N-1）归入过程轨迹：工具/思考行紧凑排列，
            // 过渡旁白折叠为单行步骤摘要，避免多轮中间废话与最终交付结论混排。
            var statusSnapshot = _stream.StatusLines.ToList();
            var intermediateText = _stream.TextBuffer.ToString();
            _committedJournal.Add(BuildCommittedIntermediateTurnJournalEntry(
                statusSnapshot, intermediateText));
            var committedLines = BuildCommittedIntermediateTurnLines(
                statusSnapshot, intermediateText, ContentWidth);
            _messageView.ReplaceStreamingPreview(committedLines);
        }

        _messageView.EndStreamingPreview();
        _stream.ResetForNextTurn();
        _messageView.BeginStreamingPreview();

        _app.Invoke(() => ActivityChanged?.Invoke("处理中"));
    }

    /// <summary>
    /// 流式期间插入已提交块（如 <see cref="AddSystem"/> 输出的 Goal 计划摘要）前，
    /// 先将当前流式预览中已累积的状态行/文本固化到 <c>_committedJournal</c> 并结束预览窗口，
    /// 待已提交块追加后再重新开启新预览窗口，避免预览重绘吞掉已提交块或重排时顺序倒置。
    /// </summary>
    private void FlushStreamingBeforeCommittedBlock()
    {
        CancelPendingStreamingRebuild();

        if (_stream.HasThinking)
            FinalizeThinkingMarker();

        if (_stream.StatusLines.Count > 0 || _stream.TextBuffer.Length > 0)
        {
            var statusSnapshot = _stream.StatusLines.ToList();
            var intermediateText = _stream.TextBuffer.ToString();
            _committedJournal.Add(BuildCommittedIntermediateTurnJournalEntry(
                statusSnapshot, intermediateText));
            var committedLines = BuildCommittedIntermediateTurnLines(
                statusSnapshot, intermediateText, ContentWidth);
            _messageView.ReplaceStreamingPreview(committedLines);
        }

        _messageView.EndStreamingPreview();
        _stream.ResetForNextTurn();
    }

    public void EndStreaming()
    {
        _logger.LogDebug(
            "[EndStreaming] pendingLines={PendingLines}, statusLines={StatusLines}, lineCountInView={LineCountInView}",
            _stream.PendingLines?.Count ?? -1, _stream.StatusLines.Count, _stream.PreviewLineCount);
        _stream.IsStreaming = false;
        StopCursorBlink();

        // Cancel any pending debounced rebuild so it doesn't fire after we've
        // already committed the final lines (which would replace the wrong window).
        CancelPendingStreamingRebuild();

        // Finalize one clickable thinking summary before building committed lines.
        if (_stream.HasThinking)
            FinalizeThinkingMarker();

        if (_stream.PendingLines is { Count: > 0 })
        {
            // Rebuild the final committed lines with proper markdown rendering.
            // During streaming, the preview used plain word-wrap for performance;
            // now we render the complete text through the Markdown renderer so
            // code blocks, headings, lists, and tables display correctly.
            _committedJournal.Add(BuildCommittedStreamJournalEntry(
                _stream.StatusLines.ToList(), _stream.TextBuffer.ToString()));
            var finalLines = BuildFinalCommittedLines();
            _messageView.ReplaceStreamingPreview(finalLines);
        }
        else if (_stream.StatusLines.Count > 0)
        {
            // Safeguard: if _stream.PendingLines is null (e.g., EndStreaming was
            // already called from an error handler) but _stream.StatusLines
            // still has uncommitted items (e.g., a FileChangeEvent that arrived
            // after the first EndStreaming), append them directly so they are
            // not lost.
            _committedJournal.Add(BuildCommittedStatusJournalEntry(_stream.StatusLines.ToList()));
            _renderer.CurrentWidth = ContentWidth;
            _messageView.AppendLines(_stream.StatusLines);
        }

        _messageView.EndStreamingPreview();
        _stream.CompleteStream();
    }

    private void CancelPendingStreamingRebuild()
    {
        if (_stream.RebuildTimer is { } token)
        {
            _app.RemoveTimeout(token);
            _stream.RebuildTimer = null;
        }
        _stream.RebuildPending = false;
    }

    /// <summary>
    /// 启动流式光标闪烁（<see cref="CursorBlinkCadenceMs"/> 周期切换可见性并重建预览行）。
    /// 流式生命周期内幂等；<see cref="EndStreaming"/> 与任何把 IsStreaming 置 false 的路径负责停止。
    /// </summary>
    private void StartCursorBlink()
    {
        if (_cursorBlinkTimer is not null) return;
        _streamingCursorVisible = true;
        _cursorBlinkTimer = _app.AddTimeout(
            TimeSpan.FromMilliseconds(CursorBlinkCadenceMs), OnCursorBlinkTick);
    }

    private void StopCursorBlink()
    {
        if (_cursorBlinkTimer is null) return;
        _app.RemoveTimeout(_cursorBlinkTimer);
        _cursorBlinkTimer = null;
    }

    private bool OnCursorBlinkTick()
    {
        if (!_stream.IsStreaming)
        {
            // 流式已由非 EndStreaming 路径（如错误中断）结束：自停计时器，
            // 下一次 BeginStreaming/AppendStreamingToken 会重新启动。
            _cursorBlinkTimer = null;
            return false;
        }

        _streamingCursorVisible = !_streamingCursorVisible;
        if (_stream.WrappedTextLines.Count > 0)
            RebuildStreamingPreview();
        return true;
    }

    /// <summary>
    /// 光标闪烁只影响预览最后一行。重建走增量补丁路径（<see cref="MessageListView.ReplaceStreamingPreview"/>）：
    /// 未变化的行原地保留，只重写闪烁的那一行，因此这里无需再单独维护「只改最后一行」的分支。
    /// </summary>

    public void AppendStreamingToken(string text)
    {
        if (!_stream.IsStreaming) return;

        // 新内容到达时光标重置为常亮（编辑器"打字时暂停闪烁"惯例），静默期由 blink tick 闪烁。
        _streamingCursorVisible = true;
        StartCursorBlink();

        _renderer.CurrentWidth = ContentWidth;

        // When the first reply token arrives after thinking, collapse the
        // expanded thinking block into a single summary line.
        if (_stream.HasThinking && !_stream.HasReplyStarted)
        {
            _stream.HasReplyStarted = true;
            FinalizeThinkingMarker();
            _app.Invoke(() => ActivityChanged?.Invoke("回复中"));
        }

        // 部分模型在 reasoning/text 交错输出时给第一段文本带前导缩进，回复起点剥掉；
        // 剥完为空的段直接丢弃（TextBuffer 保持为空，下一段仍按起点处理）。
        if (_stream.TextBuffer.Length == 0)
        {
            text = MessageFlowRenderer.TrimReplyLeadingWhitespace(text);
            if (text.Length == 0)
                return;
        }

        _stream.TextBuffer.Append(text);

        // Debounce the rebuild: fast token streams would otherwise re-wrap the
        // entire buffer on every token, causing O(n²) work and visible lag.
        // The first token after a gap rebuilds immediately so the user sees
        // the response start without the cadence delay; subsequent bursts
        // coalesce into ~60 FPS rebuilds.
        if (_stream.PreviewLineCount == 0)
        {
            RebuildStreamingPreview();
        }
        else
        {
            ScheduleStreamingRebuild();
        }
    }

    // Streaming preview rebuild is debounced via this tick. Token arrivals can
    // fire hundreds of times per second for fast models; re-wrapping the entire
    // _stream.TextBuffer on every token makes streaming O(n²) in response length
    // and causes visible lag on long answers. We coalesce token bursts into a
    // single rebuild on the next UI-loop iteration.
    private void RebuildStreamingPreview()
    {
        _stream.PendingLines = BuildStreamingPreviewLines(ContentWidth);
        _messageView.ReplaceStreamingPreview(_stream.PendingLines);
        _stream.PreviewLineCount = _messageView.StreamingPreviewLineCount;
    }

    /// <summary>
    /// 构建当前流式预览的行（状态行 + 间隔 + 按宽度换行的回复文本）。
    /// 独立成方法供 token 到达时的常规重建与宽度变化后的整体重渲共用。
    /// </summary>
    private List<FormattedLine> BuildStreamingPreviewLines(int width)
    {
        var (prevMode, prevWidth) = (_renderer.CurrentMode, _renderer.CurrentWidth);
        _renderer.CurrentWidth = width;
        try
        {
            List<FormattedLine> pending = [];

            for (var i = 0; i < _stream.StatusLines.Count; i++)
            {
                pending.Add(_stream.StatusLines[i]);
            }

            // Incremental wrap: cache completed paragraphs; only re-wrap the trailing
            // incomplete paragraph (and any newly completed ones) on each rebuild.
            var wrapWidth = width - ConversationRenderer.ContentIndent - 2;
            UpdateStreamingWrapCache(wrapWidth);

            if (_stream.WrappedTextLines.Count > 0 && pending.Count > 0)
                pending.Add(FormattedLine.Plain("", TuiPalette.BgPrimary));

            var lastIndex = _stream.WrappedTextLines.Count - 1;
            var showCursor = _stream.IsStreaming && _streamingCursorVisible && lastIndex >= 0;
            for (var i = 0; i <= lastIndex; i++)
            {
                if (showCursor && i == lastIndex)
                {
                    var withCursor = _stream.WrappedTextLines[i] + TuiGlyphs.StreamingCursor;
                    if (TextWidthHelper.GetDisplayWidth(withCursor) <= Math.Max(1, wrapWidth))
                    {
                        pending.Add(FormattedLine.Plain(
                            $"{ConversationRenderer.Indent}{withCursor}", TuiPalette.AssistantMessage));
                        continue;
                    }
                }

                pending.Add(_stream.WrappedFormattedLines[i]);
            }

            return pending;
        }
        finally
        {
            _renderer.CurrentMode = prevMode;
            _renderer.CurrentWidth = prevWidth;
        }
    }

    /// <summary>
    /// Updates the streaming word-wrap cache. Completed paragraphs (those ending
    /// with a newline in the source buffer) are wrapped once and retained; only
    /// the open trailing paragraph is re-wrapped when new tokens arrive.
    /// Width changes invalidate the entire cache.
    /// </summary>
    /// <remarks>
    /// 已完成段落直接留在 <see cref="StreamingTranscriptState.WrappedTextLines"/> 的稳定前缀里，
    /// 每次只删掉尾段再追加新尾段：旧实现每帧清空整表再回填全部已完成行，
    /// 长回复下这部分工作与回复长度成正比，且每帧都物化整个缓冲区字符串。
    /// </remarks>
    private void UpdateStreamingWrapCache(int wrapWidth)
    {
        if (wrapWidth <= 0) wrapWidth = 40;

        var buffer = _stream.TextBuffer;
        if (wrapWidth != _stream.WrappedWidth)
        {
            _stream.WrappedTextLines.Clear();
            _stream.WrappedFormattedLines.Clear();
            _stream.WrappedStableCount = 0;
            _stream.WrappedWidth = wrapWidth;
            _stream.WrappedBufferLength = 0;
            _stream.WrappedCompletedEnd = 0;
        }

        // Nothing new and width unchanged — keep existing lines.
        if (buffer.Length == _stream.WrappedBufferLength && _stream.WrappedTextLines.Count > 0)
            return;

        // Advance completed-paragraph frontier.
        var lastNl = LastIndexOfNewline(buffer);
        var completedEnd = lastNl >= 0 ? lastNl + 1 : 0;

        if (completedEnd < _stream.WrappedCompletedEnd)
        {
            // Buffer shrank (shouldn't happen mid-stream) — full rebuild.
            _stream.WrappedTextLines.Clear();
            _stream.WrappedFormattedLines.Clear();
            _stream.WrappedStableCount = 0;
            _stream.WrappedCompletedEnd = 0;
        }

        if (completedEnd > _stream.WrappedCompletedEnd)
        {
            var chunk = CopyRange(buffer, _stream.WrappedCompletedEnd, completedEnd - _stream.WrappedCompletedEnd);
            // chunk always ends with '\n'. Split yields a trailing empty element
            // representing the still-open next paragraph — skip it so the live
            // tail owns that paragraph instead of inserting a phantom blank line.
            var parts = chunk.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
            for (var i = 0; i < parts.Length - 1; i++)
            {
                if (parts[i].Length == 0)
                    AppendWrappedLine(string.Empty);
                else
                    foreach (var wrapped in ConversationRenderer.WordWrapStreaming(parts[i], wrapWidth))
                        AppendWrappedLine(wrapped);
            }

            _stream.WrappedCompletedEnd = completedEnd;
            _stream.WrappedStableCount = _stream.WrappedTextLines.Count;
        }

        // 只重算尾段：稳定前缀（含其渲染行实例）原地保留。
        var tailCount = _stream.WrappedTextLines.Count - _stream.WrappedStableCount;
        if (tailCount > 0)
        {
            _stream.WrappedTextLines.RemoveRange(_stream.WrappedStableCount, tailCount);
            _stream.WrappedFormattedLines.RemoveRange(_stream.WrappedStableCount, tailCount);
        }

        if (completedEnd < buffer.Length)
        {
            var tail = CopyRange(buffer, completedEnd, buffer.Length - completedEnd);
            foreach (var wrapped in ConversationRenderer.WordWrapStreaming(tail, wrapWidth))
                AppendWrappedLine(wrapped);
        }

        _stream.WrappedBufferLength = buffer.Length;
    }

    private void AppendWrappedLine(string text)
    {
        _stream.WrappedTextLines.Add(text);
        // 渲染行只依赖文本（缩进/配色恒定）：宽度变化会整体清空缓存，故可安全复用实例。
        _stream.WrappedFormattedLines.Add(FormattedLine.Plain(
            $"{ConversationRenderer.Indent}{text}", TuiPalette.AssistantMessage));
    }

    /// <summary>在 <see cref="StringBuilder"/> 上找最后一个换行，逐块倒序扫描，不物化整串。</summary>
    private static int LastIndexOfNewline(StringBuilder buffer)
    {
        var offset = buffer.Length;
        foreach (var chunk in buffer.GetChunks())
        {
            offset -= chunk.Length;
            var index = chunk.Span.LastIndexOf('\n');
            if (index >= 0)
                return offset + index;
        }

        return -1;
    }

    /// <summary>按区间取子串（跨块拼接），避免为取一小段而物化整个缓冲区。</summary>
    private static string CopyRange(StringBuilder buffer, int start, int length)
    {
        if (length <= 0)
            return string.Empty;

        var remaining = length;
        var cursor = 0;
        StringBuilder? parts = null;
        foreach (var chunk in buffer.GetChunks())
        {
            var chunkEnd = cursor + chunk.Length;
            if (chunkEnd <= start)
            {
                cursor = chunkEnd;
                continue;
            }

            var from = Math.Max(start, cursor) - cursor;
            var take = Math.Min(chunk.Length - from, remaining);
            if (parts is null && take == length)
                return chunk.Slice(from, take).ToString();

            parts ??= new StringBuilder(length);
            parts.Append(chunk.Slice(from, take));
            remaining -= take;
            cursor = chunkEnd;
            if (remaining == 0)
                break;
        }

        return parts?.ToString() ?? string.Empty;
    }

    /// <summary>
    /// Schedules a streaming preview rebuild on the UI loop. Multiple token
    /// arrivals within the same cadence window collapse into a single rebuild,
    /// converting the O(n²) per-token re-wrap into O(n) per cadence window.
    /// </summary>
    private void ScheduleStreamingRebuild()
    {
        if (_stream.RebuildPending) return;
        _stream.RebuildPending = true;
        _stream.RebuildTimer = _app.AddTimeout(
            TimeSpan.FromMilliseconds(StreamingRebuildCadenceMs),
            () =>
            {
                _stream.RebuildPending = false;
                _stream.RebuildTimer = null;
                if (!_stream.IsStreaming) return false;
                // Reuse the current line count as "previous" — RebuildStreamingPreview
                // replaces exactly that many trailing lines with the new preview.
                RebuildStreamingPreview();
                return false; // one-shot
            });
    }

    /// <summary>
    /// Builds the final committed line list for the just-completed streaming
    /// turn. Unlike the streaming preview (which uses plain word-wrap for
    /// performance), this routes the accumulated text buffer through the
    /// Markdown renderer so code blocks, headings, lists, and tables render
    /// with proper formatting and syntax highlighting.
    /// </summary>
    private IReadOnlyList<FormattedLine> BuildFinalCommittedLines()
        => BuildCommittedStreamLines(_stream.StatusLines, _stream.TextBuffer.ToString(), ContentWidth);

    /// <summary>
    /// 为一段已提交流构造重渲日志条目：状态行按提交时的样子原样保留
    /// （含展开 tag，宽度无关），回复文本按新宽度经 markdown 渲染器重渲。
    /// </summary>
    private Func<int, IReadOnlyList<FormattedLine>> BuildCommittedStreamJournalEntry(
        IReadOnlyList<FormattedLine> statusLines, string text)
        => MemoizeByWidth(width => BuildCommittedStreamLines(statusLines, text, width));

    /// <summary>
    /// 为一段仅含状态行（无回复文本）的已提交流构造重渲日志条目。
    /// </summary>
    private Func<int, IReadOnlyList<FormattedLine>> BuildCommittedStatusJournalEntry(
        IReadOnlyList<FormattedLine> statusLines)
        => _ => statusLines;

    private Func<int, IReadOnlyList<FormattedLine>> BuildCommittedIntermediateTurnJournalEntry(
        IReadOnlyList<FormattedLine> statusLines, string text)
        => MemoizeByWidth(width => BuildCommittedIntermediateTurnLines(statusLines, text, width));

    /// <summary>
    /// 构建中间工具轮（ContinueStreaming）的紧凑轨迹行：
    /// 工具与思考行紧凑叠放；超过 2 行的长篇过渡旁白自动收起为可展开的单行摘要。
    /// </summary>
    private IReadOnlyList<FormattedLine> BuildCommittedIntermediateTurnLines(
        IReadOnlyList<FormattedLine> statusLines, string text, int width)
    {
        List<FormattedLine> lines = [.. statusLines];

        if (!string.IsNullOrWhiteSpace(text))
        {
            var trimmed = text.Trim();
            var wrapWidth = Math.Max(20, width - ConversationRenderer.ContentIndent - 2);
            List<string> wrapped = [];
            foreach (var paragraph in trimmed.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
            {
                if (paragraph.Length == 0)
                    continue;
                wrapped.AddRange(ConversationRenderer.WordWrapStreaming(paragraph, wrapWidth));
            }

            if (wrapped.Count > 2)
            {
                var preview = TextWidthHelper.TruncateByWidth(wrapped[0], Math.Max(16, wrapWidth - 16));
                var remainingText = string.Join('\n', wrapped.Skip(1));
                var tag = ThinkingLineTag.Create(remainingText, isExpanded: false);
                lines.Add(FormattedLine.FromSegmentsWithTag(
                [
                    new LineSegment(ConversationRenderer.Indent, TuiPalette.BgPrimary),
                    new LineSegment($"{TuiGlyphs.Collapsed} ", TuiPalette.FgMuted),
                    new LineSegment(preview, TuiPalette.FgSecondary),
                    new LineSegment($" \u00b7 +{wrapped.Count - 1} 行", TuiPalette.FgMuted),
                ], tag));
            }
            else
            {
                foreach (var w in wrapped)
                    lines.Add(FormattedLine.Plain($"{ConversationRenderer.Indent}{w}", TuiPalette.FgSecondary));
            }
        }

        return lines;
    }

    /// <summary>
    /// 构建一段已提交流的最终行：状态行紧凑排列 + 回复文本的 markdown 渲染。
    /// </summary>
    private IReadOnlyList<FormattedLine> BuildCommittedStreamLines(
        IReadOnlyList<FormattedLine> statusLines, string text, int width)
    {
        List<FormattedLine> lines = [.. statusLines];

        // Re-render the accumulated text through the Markdown renderer.
        if (!string.IsNullOrWhiteSpace(text))
        {
            if (lines.Count > 0)
                lines.Add(FormattedLine.Plain("", TuiPalette.BgPrimary));
            var (prevMode, prevWidth) = (_renderer.CurrentMode, _renderer.CurrentWidth);
            _renderer.CurrentWidth = width;
            try
            {
                _renderer.AppendAssistantText(lines, text);
            }
            finally
            {
                _renderer.CurrentMode = prevMode;
                _renderer.CurrentWidth = prevWidth;
            }
        }

        return lines;
    }
}
