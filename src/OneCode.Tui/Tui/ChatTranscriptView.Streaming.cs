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
            _stream.StatusLines.Count, _messageView.StreamingPreviewLineCount);
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
            _stream.PendingLines?.Count ?? -1, _stream.StatusLines.Count, _messageView.StreamingPreviewLineCount);
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
            // 收尾按同一渲染器重渲一次并登记按宽度重渲的日志条目。
            // 预览与提交共用 MarkdownRenderer、共用宽度，形态因此不跳变。
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
        if (_stream.TextBuffer.Length > 0)
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
        if (_messageView.StreamingPreviewLineCount == 0)
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
    }

    /// <summary>
    /// 构建当前流式预览的行（状态行 + 间隔 + markdown 渲染后的回复）。
    /// 独立成方法供 token 到达时的常规重建与宽度变化后的整体重渲共用。
    /// </summary>
    /// <remarks>
    /// 流式期间走与提交时<b>同一个</b> markdown 渲染器：用户看到的形态从第一个 token 起
    /// 就是最终形态，不再出现「生成时全是原始标记、结束时整段跳变」。
    /// 已完成块（以围栏外的空行分隔）渲染一次即缓存，每帧只重渲最后一个未闭合块。
    /// </remarks>
    private List<FormattedLine> BuildStreamingPreviewLines(int width)
    {
        List<FormattedLine> pending = [.. _stream.StatusLines];

        var text = _stream.TextBuffer.ToString();
        if (text.Length == 0)
            return pending;

        UpdateCommittedBlocks(text, width);

        if (pending.Count > 0)
            pending.Add(FormattedLine.Plain("", TuiPalette.BgPrimary));
        pending.AddRange(_stream.CommittedBlocks);

        var tail = text[_stream.CommittedBufferEnd..];
        if (!string.IsNullOrWhiteSpace(tail))
            pending.AddRange(MessageFlowRenderer.RenderMarkdownLines(tail, width));

        if (_stream.IsStreaming && _streamingCursorVisible && pending.Count > 0)
        {
            var last = pending[^1];
            // 光标落在最后一行行尾；整行已占满时不追加（会被绘制层裁掉，反而看起来少一个字）。
            if (TextWidthHelper.GetDisplayWidth(last.FullText) + 1 < width)
            {
                pending[^1] = last.Segments is { Count: > 0 } segments
                    ? FormattedLine.FromSegments([.. segments, new LineSegment(TuiGlyphs.StreamingCursor, last.Color)])
                    : FormattedLine.Plain(last.FullText + TuiGlyphs.StreamingCursor, last.Color);
            }
        }

        return pending;
    }

    /// <summary>
    /// 把「已闭合的 markdown 块」渲染结果追加到缓存，只处理新增区间。
    /// 宽度变化即整体失效：缓存的是按宽度排版好的行，换宽度必须全部重排。
    /// </summary>
    private void UpdateCommittedBlocks(string text, int width)
    {
        if (text.Length < _stream.CommittedBufferEnd || _stream.BlocksWidth != width)
        {
            _stream.BlocksWidth = width;
            _stream.CommittedBlocks.Clear();
            _stream.CommittedBufferEnd = 0;
        }

        var split = FindBlockSplit(text, _stream.CommittedBufferEnd);
        if (split <= _stream.CommittedBufferEnd)
            return;

        _stream.CommittedBlocks.AddRange(
            MessageFlowRenderer.RenderMarkdownLines(text[_stream.CommittedBufferEnd..split], width));
        _stream.CommittedBufferEnd = split;
    }

    /// <summary>
    /// 返回自 <paramref name="from"/> 起、最后一个「可安全切块」的位置：围栏代码块之外的
    /// 最后一个空行之后。围栏内的空行不能切——否则代码块会被切成多个互不相连的边框。
    /// </summary>
    /// <remarks>
    /// <paramref name="from"/> 恒落在两个块之间（上一次的切块点），此处围栏一定是关闭的，
    /// 因此可以只扫新增区间，避免每帧重扫整段回复。
    /// </remarks>
    internal static int FindBlockSplit(string text, int from = 0)
    {
        var split = from;
        var inFence = false;
        var lineStart = Math.Clamp(from, 0, text.Length);

        while (lineStart < text.Length)
        {
            var newline = text.IndexOf('\n', lineStart);
            var lineEnd = newline < 0 ? text.Length : newline;
            var nextStart = newline < 0 ? text.Length : newline + 1;

            var line = text.AsSpan(lineStart, lineEnd - lineStart).Trim();
            if (line.StartsWith("```") || line.StartsWith("~~~"))
                inFence = !inFence;
            else if (!inFence && line.IsEmpty)
                split = nextStart;

            lineStart = nextStart;
        }

        return split;
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
    /// 构建本轮结束时的已提交行（状态行 + 回复的 markdown 渲染）。
    /// 与流式预览同一个渲染器、同一宽度：收尾只是把预览窗口封印成历史，
    /// 不重新排版，因此不存在形态跳变。
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

        if (!string.IsNullOrWhiteSpace(text))
        {
            if (lines.Count > 0)
                lines.Add(FormattedLine.Plain("", TuiPalette.BgPrimary));
            lines.AddRange(MessageFlowRenderer.RenderMarkdownLines(text, width));
        }

        return lines;
    }
}
