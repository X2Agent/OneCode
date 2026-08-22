namespace OneCode.App.Tui;

/// <summary>
/// Streaming render state machine extracted from ChatTranscriptView.
/// Manages the live streaming preview lifecycle: begin/continue/end streaming,
/// token append with debounced rebuild, and final markdown commit.
/// </summary>
public sealed partial class ChatTranscriptView
{
    private const int StreamingRebuildCadenceMs = 16; // ~60 FPS cap

    public void BeginStreaming()
    {
        _stream.ResetForNewStream(new AssistantMessage(
            Id: Guid.NewGuid().ToString("N"),
            Content: new List<ContentBlock>(),
            Timestamp: DateTimeOffset.UtcNow));
        _messageView.BeginStreamingPreview();

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
            // 日志条目捕获不可变快照（StatusLines 副本 + 文本），ResetForNextTurn
            // 会清空 _stream——闭包不能引用可变状态。
            _committedJournal.Add(BuildCommittedStreamJournalEntry(
                _stream.StatusLines.ToList(), _stream.TextBuffer.ToString()));
            var committedLines = BuildFinalCommittedLines();
            _messageView.ReplaceStreamingPreview(committedLines);
        }

        _messageView.EndStreamingPreview();
        _stream.ResetForNextTurn();
        _messageView.BeginStreamingPreview();

        _app.Invoke(() => ActivityChanged?.Invoke("处理中"));
    }

    public void EndStreaming()
    {
        _logger.LogDebug(
            "[EndStreaming] pendingLines={PendingLines}, statusLines={StatusLines}, lineCountInView={LineCountInView}",
            _stream.PendingLines?.Count ?? -1, _stream.StatusLines.Count, _stream.PreviewLineCount);
        _stream.IsStreaming = false;

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
            // still has uncommitted items (e.g., a TuiFileChange that arrived
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

    public void AppendStreamingToken(string text)
    {
        if (!_stream.IsStreaming) return;

        _renderer.CurrentWidth = ContentWidth;

        // When the first reply token arrives after thinking, collapse the
        // expanded thinking block into a single summary line.
        if (_stream.HasThinking && !_stream.HasReplyStarted)
        {
            _stream.HasReplyStarted = true;
            FinalizeThinkingMarker();
            _app.Invoke(() => ActivityChanged?.Invoke("回复中"));
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
            var pending = new List<FormattedLine>();

            for (var i = 0; i < _stream.StatusLines.Count; i++)
            {
                pending.Add(_stream.StatusLines[i]);
                if (i < _stream.StatusLines.Count - 1
                    && _stream.StatusLines[i].Tag is not ThinkingDetailLineTag
                    && _stream.StatusLines[i + 1].Tag is not ThinkingDetailLineTag)
                {
                    pending.Add(FormattedLine.Plain("", TuiPalette.BgPrimary));
                }
            }

            // Incremental wrap: cache completed paragraphs; only re-wrap the trailing
            // incomplete paragraph (and any newly completed ones) on each rebuild.
            var fullText = _stream.TextBuffer.ToString();
            var wrapWidth = width - ConversationRenderer.ContentIndent - 2;
            UpdateStreamingWrapCache(fullText, wrapWidth);

            if (_stream.WrappedTextLines.Count > 0 && pending.Count > 0)
                pending.Add(FormattedLine.Plain("", TuiPalette.BgPrimary));
            foreach (var line in _stream.WrappedTextLines)
            {
                pending.Add(FormattedLine.Plain($"{ConversationRenderer.Indent}{line}", TuiPalette.AssistantMessage));
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
    private void UpdateStreamingWrapCache(string fullText, int wrapWidth)
    {
        if (wrapWidth <= 0) wrapWidth = 40;

        if (wrapWidth != _stream.WrappedWidth)
        {
            _stream.WrappedCompleteLines.Clear();
            _stream.WrappedCompletedEnd = 0;
            _stream.WrappedWidth = wrapWidth;
            _stream.WrappedBufferLength = 0;
        }

        // Nothing new and width unchanged — keep existing lines.
        if (fullText.Length == _stream.WrappedBufferLength && _stream.WrappedTextLines.Count > 0)
            return;

        // Advance completed-paragraph frontier.
        var lastNl = fullText.LastIndexOf('\n');
        var completedEnd = lastNl >= 0 ? lastNl + 1 : 0;

        if (completedEnd < _stream.WrappedCompletedEnd)
        {
            // Buffer shrank (shouldn't happen mid-stream) — full rebuild.
            _stream.WrappedCompleteLines.Clear();
            _stream.WrappedCompletedEnd = 0;
        }

        if (completedEnd > _stream.WrappedCompletedEnd)
        {
            var chunk = fullText[_stream.WrappedCompletedEnd..completedEnd];
            // chunk always ends with '\n'. Split yields a trailing empty element
            // representing the still-open next paragraph — skip it so the live
            // tail owns that paragraph instead of inserting a phantom blank line.
            var parts = chunk.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
            for (var i = 0; i < parts.Length - 1; i++)
            {
                if (parts[i].Length == 0)
                    _stream.WrappedCompleteLines.Add("");
                else
                    _stream.WrappedCompleteLines.AddRange(
                        ConversationRenderer.WordWrapStreaming(parts[i], wrapWidth));
            }
            _stream.WrappedCompletedEnd = completedEnd;
        }

        _stream.WrappedTextLines.Clear();
        _stream.WrappedTextLines.AddRange(_stream.WrappedCompleteLines);

        if (completedEnd < fullText.Length)
        {
            var tail = fullText[completedEnd..];
            _stream.WrappedTextLines.AddRange(
                ConversationRenderer.WordWrapStreaming(tail, wrapWidth));
        }

        _stream.WrappedBufferLength = fullText.Length;
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
        => width => BuildCommittedStreamLines(statusLines, text, width);

    /// <summary>
    /// 为一段仅含状态行（无回复文本）的已提交流构造重渲日志条目。
    /// </summary>
    private Func<int, IReadOnlyList<FormattedLine>> BuildCommittedStatusJournalEntry(
        IReadOnlyList<FormattedLine> statusLines)
        => _ => statusLines;

    /// <summary>
    /// 构建一段已提交流的最终行：状态行（含间隔空行）+ 回复文本的 markdown 渲染。
    /// </summary>
    private IReadOnlyList<FormattedLine> BuildCommittedStreamLines(
        IReadOnlyList<FormattedLine> statusLines, string text, int width)
    {
        var lines = new List<FormattedLine>();

        // Preserve tool-call / thinking / notice lines with uniform spacing.
        foreach (var line in statusLines)
        {
            lines.Add(line);
            lines.Add(FormattedLine.Plain("", TuiPalette.BgPrimary));
        }
        if (statusLines.Count > 0 && lines.Count > 0)
            lines.RemoveAt(lines.Count - 1);

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
