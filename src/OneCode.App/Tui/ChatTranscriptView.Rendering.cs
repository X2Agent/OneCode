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
            var charCount = thought?.Length ?? 0;
            var marker = FormattedLine.FromSegments(new[]
            {
                new LineSegment($"{ConversationRenderer.Indent}", TuiPalette.BgPrimary),
                new LineSegment($"{TuiGlyphs.Collapsed} Thought ({charCount} chars)", TuiPalette.FgMuted),
            });
            AppendCommittedBlock(_ => new[] { marker }, withSpacing: false);
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

        RebuildStreamingPreview();
    }

    /// <summary>
    /// Rebuilds the thinking header in _stream.StatusLines.
    /// Detail lines are materialized by MessageListView from <see cref="ThinkingLineTag.Content"/>
    /// so a preview rebuild cannot split one thought into stacked headers.
    /// </summary>
    private void RebuildExpandedThinkingLines()
    {
        if (_stream.ThinkingSummaryLineIndex < 0) return;

        ReplaceThinkingSpan(new List<FormattedLine>
        {
            BuildThinkingSummary("Thinking", isExpanded: true),
        });
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

        var lineIndex = _stream.StatusLines.Count;
        // 使用 ToolResultSummarizer 格式化目标显示
        var formattedTarget = ToolResultSummarizer.FormatTarget(name, toolInput);
        _stream.StatusLines.Add(MessageFlowRenderer.MakeToolLine(name, formattedTarget, null));
        _stream.ToolTracker.RegisterStart(toolId, lineIndex);
        _app.Invoke(() => ActivityChanged?.Invoke($"执行 {name}"));
        RebuildStreamingPreview();
    }

    public void AddToolDone(string name, bool isError, string? toolInput, string result, string toolId)
    {
        _logger.LogDebug(
            "[AddToolDone] streaming={Streaming}, name={Name}, toolId={ToolId}, err={Error}",
            _stream.IsStreaming, name, toolId, isError);
        if (_stream.IsStreaming)
        {
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

            RebuildStreamingPreview();
            return;
        }

        var trm = new ToolResultMessage(
            Id: Guid.NewGuid().ToString("N"),
            ToolUseId: toolId,
            ToolName: name,
            Content: result,
            IsError: isError,
            Timestamp: DateTimeOffset.UtcNow);

        AppendCommittedBlock(RenderMessageJournalEntry(trm));

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

        _stream.StatusLines.Add(ConversationRenderer.MakeStreamingNotice(text, color ?? TuiPalette.SystemMessage));
        RebuildStreamingPreview();
    }

    /// <summary>
    /// TEAM 成员发言块：说话人行 + 最多 3 行预览（折叠式呈现）。
    /// 完整内容仍保留在 Delivery 证据中，主对话不被长文本刷屏。
    /// </summary>
    public void AddTeamSpeech(string agentName, string content)
    {
        const int PreviewLineCount = 3;
        var speechLines = (content ?? string.Empty)
            .Replace("\r\n", "\n")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var lines = new List<FormattedLine>
        {
            FormattedLine.FromSegments(new[]
            {
                new LineSegment($"{ConversationRenderer.Indent}", TuiPalette.BgPrimary),
                new LineSegment($"{TuiGlyphs.Collapsed} {agentName}", TuiPalette.FgMuted),
            }),
        };
        foreach (var line in speechLines.Take(PreviewLineCount))
            lines.Add(FormattedLine.Plain($"{ConversationRenderer.Indent}  {line}", TuiPalette.FgSecondary));
        if (speechLines.Length > PreviewLineCount)
            lines.Add(FormattedLine.FromSegments(new[]
            {
                new LineSegment($"{ConversationRenderer.Indent}", TuiPalette.BgPrimary),
                new LineSegment($"… 共 {speechLines.Length} 行，完整内容见交付报告", TuiPalette.FgMuted),
            }));

        AppendCommittedBlock(_ => lines, withSpacing: false);
    }

    public void UpdateBuildRunStatus(TuiBuildRunState state)
    {
        if (!_stream.IsStreaming)
        {
            AppendCommittedBlock(width => ChatBlockRenderers.RenderBuildRunPanel(state, width));
            return;
        }

        var lines = ChatBlockRenderers.RenderBuildRunPanel(state, ContentWidth);
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
        RebuildStreamingPreview();
    }

    public void AddBuildDeliveryCard(OneCode.Core.Build.BuildRunResult result)
    {
        if (_stream.IsStreaming)
            AddFormattedLines(ChatBlockRenderers.RenderBuildDeliveryCard(result, ContentWidth));
        else
            AppendCommittedBlock(width => ChatBlockRenderers.RenderBuildDeliveryCard(result, width));
    }

    public void UpdateModeProgress(TuiModeProgress progress)
    {
        if (!_stream.IsStreaming)
        {
            AppendCommittedBlock(width => ChatBlockRenderers.RenderModeProgress(progress, width));
            return;
        }

        var lines = ChatBlockRenderers.RenderModeProgress(progress, ContentWidth);
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
        RebuildStreamingPreview();
    }

    /// <summary>
    /// 文件修改 Diff 展示 — 接入 ChatBlockRenderers.RenderDiffBlock。
    /// EditTransactionMiddleware 检测到 Write/Edit 后发射 TuiFileChange 事件，
    /// 此方法渲染 +N/-M 行的 Diff 块。Diff 行与宽度无关，日志条目直接持有渲染结果。
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
        if (_stream.IsStreaming)
            AddFormattedLines(lines);
        else
            AppendCommittedBlock(_ => lines);
    }

    /// <summary>流式期间追加状态行（宽度相关块由提交时的日志条目负责重渲）。</summary>
    private void AddFormattedLines(IReadOnlyList<FormattedLine> lines)
    {
        foreach (var line in lines)
            _stream.StatusLines.Add(line);
        RebuildStreamingPreview();
    }
}
