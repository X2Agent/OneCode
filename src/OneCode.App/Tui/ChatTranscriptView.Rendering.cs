namespace OneCode.App.Tui;

/// <summary>
/// Thinking, tool-call, and Build event rendering extracted from ChatTranscriptView.
/// Manages inline thinking expand/collapse, tool start/done lines, streaming notices,
/// BuildRun status panels, mode progress, and file-change diff blocks during streaming.
/// </summary>
public sealed partial class ChatTranscriptView
{
    public void AddThinking(string thought)
    {
        if (!_stream.IsStreaming)
        {
            // 非流式状态下收到 thinking delta — 作为独立标记行追加，不静默丢弃。
            // 正常流程不应走到这里，但防御性处理避免内容丢失。
            _renderer.CurrentWidth = ContentWidth;
            var charCount = thought?.Length ?? 0;
            _messageView.AppendLines(new[]
            {
                FormattedLine.FromSegments(new[]
                {
                    new LineSegment($"{ConversationRenderer.Indent}", TuiPalette.BgPrimary),
                    new LineSegment($"{TuiGlyphs.Collapsed} Thought ({charCount} chars)", TuiPalette.FgMuted),
                })
            });
            return;
        }

        // During streaming, show thinking content expanded (live updating).
        // When the reply text starts (AppendStreamingToken), the thinking
        // marker is collapsed into a summary line.
        _stream.ThinkingBuffer.Append(thought);

        if (!_stream.HasThinking)
        {
            _stream.HasThinking = true;
            _stream.ThinkingStartTick = System.Diagnostics.Stopwatch.GetTimestamp();
            _stream.ThinkingSummaryLineIndex = _stream.StatusLines.Count;
            _app.Invoke(() => ActivityChanged?.Invoke("思考中"));
        }

        RebuildExpandedThinkingLines();

        RebuildStreamingPreview(_stream.PreviewLineCount);
    }

    /// <summary>
    /// Rebuilds _stream.StatusLines with an expanded thinking block:
    /// summary header (▼) + wrapped thinking content lines.
    /// Called on every thinking delta during streaming.
    /// Only replaces the tracked thinking span — tool/notice lines after it stay.
    /// </summary>
    private void RebuildExpandedThinkingLines()
    {
        if (_stream.ThinkingSummaryLineIndex < 0) return;

        var newLines = new List<FormattedLine>
        {
            BuildThinkingSummary("Thinking", isExpanded: true),
        };

        var thinking = _stream.ThinkingBuffer.ToString();
        var maxWidth = Math.Max(20, ContentWidth - ConversationRenderer.ContentIndent - 2);
        foreach (var line in thinking.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            foreach (var w in TextWidthHelper.WordWrapByWidth(line, maxWidth))
            {
                newLines.Add(FormattedLine.Plain(
                    $"{ConversationRenderer.Indent}  {w}",
                    TuiPalette.FgSecondary));
            }
        }

        ReplaceThinkingSpan(newLines);
    }

    /// <summary>
    /// Collapses the expanded thinking block in _stream.StatusLines back to
    /// a single summary line with the final duration. Called when the reply
    /// text starts streaming or when streaming ends.
    /// </summary>
    private void FinalizeThinkingMarker()
    {
        if (!_stream.HasThinking || _stream.ThinkingSummaryLineIndex < 0) return;

        var durationStr = StreamingToolTracker.FormatDuration(_stream.ThinkingStartTick);
        ReplaceThinkingSpan(new List<FormattedLine>
        {
            BuildThinkingSummary($"Thought for {durationStr}"),
        });
    }

    /// <summary>
    /// Removes the current thinking span (if any) and inserts <paramref name="newLines"/>
    /// at the same index, shifting pending tool line indices by the size delta.
    /// </summary>
    private void ReplaceThinkingSpan(List<FormattedLine> newLines)
    {
        var index = _stream.ThinkingSummaryLineIndex;
        var oldCount = _stream.ThinkingBlockLineCount;

        if (oldCount > 0 && index + oldCount <= _stream.StatusLines.Count)
            _stream.StatusLines.RemoveRange(index, oldCount);
        else if (oldCount > 0)
        {
            // Span was corrupted (e.g. prior wipe). Drop trailing lines from index.
            var available = Math.Max(0, _stream.StatusLines.Count - index);
            if (available > 0)
                _stream.StatusLines.RemoveRange(index, available);
            oldCount = available;
        }

        _stream.StatusLines.InsertRange(index, newLines);
        var newCount = newLines.Count;
        _stream.ToolTracker.ShiftLineIndicesFrom(index + oldCount, newCount - oldCount);
        _stream.ThinkingBlockLineCount = newCount;
    }

    private FormattedLine BuildThinkingSummary(string title, bool isExpanded = false)
    {
        var thinking = _stream.ThinkingBuffer.ToString();
        var tag = new ThinkingLineTag(thinking, IsExpanded: isExpanded);
        var icon = isExpanded ? TuiGlyphs.Expanded : TuiGlyphs.Collapsed;
        return FormattedLine.FromSegmentsWithTag(new[]
        {
            new LineSegment($"{ConversationRenderer.Indent}", TuiPalette.BgPrimary),
            new LineSegment($"{icon} {title}", TuiPalette.FgMuted),
        }, tag);
    }

    public void AddToolStart(string name, string? toolInput, string toolId)
    {
        _logger.LogDebug(
            "[AddToolStart] streaming={Streaming}, name={Name}, toolId={ToolId}",
            _stream.IsStreaming, name, toolId);
        if (!_stream.IsStreaming) return;

        var prevCount = _stream.PreviewLineCount;
        var lineIndex = _stream.StatusLines.Count;
        // 使用 ToolResultSummarizer 格式化目标显示
        var formattedTarget = ToolResultSummarizer.FormatTarget(name, toolInput);
        _stream.StatusLines.Add(MessageFlowRenderer.MakeToolLine(name, formattedTarget, null));
        _stream.ToolTracker.RegisterStart(toolId, lineIndex);
        _app.Invoke(() => ActivityChanged?.Invoke($"执行 {name}"));
        RebuildStreamingPreview(prevCount);
    }

    public void AddToolDone(string name, bool isError, string? toolInput, string result, string toolId)
    {
        _logger.LogDebug(
            "[AddToolDone] streaming={Streaming}, name={Name}, toolId={ToolId}, err={Error}",
            _stream.IsStreaming, name, toolId, isError);
        if (_stream.IsStreaming)
        {
            var prevCount = _stream.PreviewLineCount;

            // 按 ToolId 精确匹配
            if (_stream.ToolTracker.TryMatchByToolId(toolId, out var pending)
                && pending.LineIndex < _stream.StatusLines.Count)
            {
                var durationStr = $"({StreamingToolTracker.FormatDuration(pending.StartTick)})";
                _stream.StatusLines[pending.LineIndex] = ConversationRenderer.MakeCompletedToolLine(name, isError, toolInput, durationStr, result);
            }
            // 已见过的 ToolId（ContinueStreaming 已提交到历史）— 跳过去重
            else if (_stream.ToolTracker.WasSeen(toolId))
            {
                // ToolDone for an already-committed start line — skip to avoid duplicate.
            }

            if (isError && !string.IsNullOrWhiteSpace(result))
                _stream.StatusLines.Add(ConversationRenderer.MakeStreamingNotice(result, TuiPalette.Error));

            RebuildStreamingPreview(prevCount);
            return;
        }

        _renderer.CurrentWidth = ContentWidth;
        var trm = new ToolResultMessage(
            Id: Guid.NewGuid().ToString("N"),
            ToolUseId: toolId,
            ToolName: name,
            Content: result,
            IsError: isError,
            Timestamp: DateTimeOffset.UtcNow);

        var lines = _renderer.RenderMessage(trm);
        AppendWithSpacing(lines);

        _stream.TextBuffer.Clear();
        _stream.Assistant = null;
    }

    public void AddStreamingNotice(string text, Color? color = null)
    {
        if (!_stream.IsStreaming)
        {
            AddSystem(text);
            return;
        }

        var prevCount = _stream.PreviewLineCount;
        _stream.StatusLines.Add(ConversationRenderer.MakeStreamingNotice(text, color ?? TuiPalette.SystemMessage));
        RebuildStreamingPreview(prevCount);
    }

    public void UpdateBuildRunStatus(TuiBuildRunState state)
    {
        var lines = ChatBlockRenderers.RenderBuildRunPanel(state, ContentWidth);
        if (!_stream.IsStreaming)
        {
            AddFormattedLines(lines);
            return;
        }

        var previousLineCount = _stream.PreviewLineCount;
        if (_stream.BuildRunStatusLineIndex >= 0)
        {
            var oldCount = _stream.BuildRunStatusLineCount;
            _stream.StatusLines.RemoveRange(_stream.BuildRunStatusLineIndex, oldCount);
            _stream.StatusLines.InsertRange(_stream.BuildRunStatusLineIndex, lines);
            var delta = lines.Count - oldCount;
            if (_stream.ThinkingSummaryLineIndex >= _stream.BuildRunStatusLineIndex + oldCount)
                _stream.ThinkingSummaryLineIndex += delta;
            _stream.ToolTracker.ShiftLineIndicesFrom(_stream.BuildRunStatusLineIndex + oldCount, delta);
        }
        else
        {
            _stream.BuildRunStatusLineIndex = _stream.StatusLines.Count;
            _stream.StatusLines.AddRange(lines);
        }
        _stream.BuildRunStatusLineCount = lines.Count;
        RebuildStreamingPreview(previousLineCount);
    }

    public void AddBuildDeliveryCard(OneCode.Core.Build.BuildRunResult result) =>
        AddFormattedLines(ChatBlockRenderers.RenderBuildDeliveryCard(result, ContentWidth));

    public void UpdateModeProgress(TuiModeProgress progress)
    {
        var lines = ChatBlockRenderers.RenderModeProgress(progress, ContentWidth);
        if (!_stream.IsStreaming)
        {
            AddFormattedLines(lines);
            return;
        }

        var previousLineCount = _stream.PreviewLineCount;
        if (_stream.ModeProgressLineIndex >= 0)
        {
            var oldCount = _stream.ModeProgressLineCount;
            _stream.StatusLines.RemoveRange(_stream.ModeProgressLineIndex, oldCount);
            _stream.StatusLines.InsertRange(_stream.ModeProgressLineIndex, lines);
            var delta = lines.Count - oldCount;
            if (_stream.BuildRunStatusLineIndex >= _stream.ModeProgressLineIndex + oldCount)
                _stream.BuildRunStatusLineIndex += delta;
            if (_stream.ThinkingSummaryLineIndex >= _stream.ModeProgressLineIndex + oldCount)
                _stream.ThinkingSummaryLineIndex += delta;
            _stream.ToolTracker.ShiftLineIndicesFrom(_stream.ModeProgressLineIndex + oldCount, delta);
        }
        else
        {
            _stream.ModeProgressLineIndex = _stream.StatusLines.Count;
            _stream.StatusLines.AddRange(lines);
        }
        _stream.ModeProgressLineCount = lines.Count;
        RebuildStreamingPreview(previousLineCount);
    }

    /// <summary>
    /// 文件修改 Diff 展示 — 接入 ChatBlockRenderers.RenderDiffBlock。
    /// EditTransactionMiddleware 检测到 Write/Edit 后发射 TuiFileChange 事件，
    /// 此方法渲染 +N/-M 行的 Diff 块。
    /// </summary>
    public void AddFileChange(string fileName, IReadOnlyList<string> addedLines, IReadOnlyList<string> removedLines)
    {
        _logger.LogDebug(
            "[AddFileChange] streaming={Streaming}, statusLines={StatusLines}, lineCountInView={LineCountInView}, file={File}, +{Added}/-{Removed}",
            _stream.IsStreaming, _stream.StatusLines.Count, _stream.PreviewLineCount, fileName, addedLines.Count, removedLines.Count);
        var lines = ChatBlockRenderers.RenderDiffBlock(
            fileName, addedLines, removedLines,
            addedSummary: addedLines.Count,
            removedSummary: removedLines.Count);
        AddFormattedLines(lines);
    }

    private void AddFormattedLines(IReadOnlyList<FormattedLine> lines)
    {
        if (_stream.IsStreaming)
        {
            var prevCount = _stream.PreviewLineCount;
            foreach (var line in lines)
                _stream.StatusLines.Add(line);
            RebuildStreamingPreview(prevCount);
        }
        else
        {
            _renderer.CurrentWidth = ContentWidth;
            AppendWithSpacing(lines);
        }
    }
}
