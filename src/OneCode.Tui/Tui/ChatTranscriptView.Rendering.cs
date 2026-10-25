namespace OneCode.Tui;

/// <summary>
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

        ReplaceThinkingSpan([
            BuildThinkingSummary("Thinking", isExpanded: true),
        ]);
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
        ReplaceThinkingSpan([
            BuildThinkingSummary($"Thought for {durationStr}"),
        ]);
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

    public void AddToolStart(string name, string? toolInput, string toolId, string? agentName = null)
    {
        _logger.LogDebug(
            "[AddToolStart] streaming={Streaming}, name={Name}, toolId={ToolId}, agent={Agent}",
            _stream.IsStreaming, name, toolId, agentName);
        if (!_stream.IsStreaming) return;

        // 进入工具执行阶段时立即将展开的思考块折叠为单行耗时摘要，
        // 避免长思考块在工具执行期间持续占据视口高度。
        if (_stream.HasThinking)
            FinalizeThinkingMarker();

        var lineIndex = _stream.StatusLines.Count;
        // 使用 ToolResultSummarizer 格式化目标显示
        var formattedTarget = ToolResultSummarizer.FormatTarget(name, toolInput);
        var isAgentTool = ToolResultSummarizer.NormalizeName(name) == "agent";
        // 展开显示目标文案（Agent 输入已收窄为 "代理名 · 描述"）；
        // 运行中的实时统计由 UpdateSubagentProgress 经 tag.LiveProgress 接管，
        // 完成后的 Args/Result 详情由 AddToolDone 替换整行接管。
        _stream.StatusLines.Add(MessageFlowRenderer.MakeToolLine(
            name, formattedTarget, null, agentName: agentName, maxWidth: ContentWidth));
        _stream.ToolTracker.RegisterStart(toolId, lineIndex);
        // Agent 工具（子代理 fork）登记进度行：SubagentProgress 到达时原位更新头行 + 活动行。
        // toolInput 是 ToolStart 通道的摘要（Agent 已收窄为 agent + description 紧凑 JSON），
        // 容错提取保证截断场景仍能取到代理名。
        if (isAgentTool
            && ToolResultSummarizer.ExtractAgentNameTolerant(toolInput) is { Length: > 0 } forkAgent)
        {
            _stream.ToolTracker.RegisterSubagentLine(forkAgent, lineIndex, formattedTarget ?? "");
        }
        _app.Invoke(() => ActivityChanged?.Invoke(
            string.IsNullOrWhiteSpace(agentName) ? $"执行 {name}" : $"[{agentName}] 执行 {name}"));
        RebuildStreamingPreview();
    }

    public void AddToolDone(string name, bool isError, string? toolInput, string result, string toolId, string? agentName = null)
    {
        _logger.LogDebug(
            "[AddToolDone] streaming={Streaming}, name={Name}, toolId={ToolId}, err={Error}, agent={Agent}",
            _stream.IsStreaming, name, toolId, isError, agentName);
        // Agent 工具完成即注销进度行（注册先于 streaming 判断，非流式回放同样要清理）。
        // 与 AddToolStart 共用容错提取，保证注册/注销的代理名取法一致。
        if (ToolResultSummarizer.NormalizeName(name) == "agent"
            && ToolResultSummarizer.ExtractAgentNameTolerant(toolInput) is { Length: > 0 } doneAgent)
        {
            // 先摘实时活动行再注销，避免残留一行孤儿进度。
            if (_stream.ToolTracker.TryGetSubagentLine(doneAgent, out var doneState))
                RemoveSubagentActivityLine(doneState);
            _stream.ToolTracker.RemoveSubagentLine(doneAgent);
        }
        if (_stream.IsStreaming)
        {
            // 按 ToolId 精确匹配
            var matched = _stream.ToolTracker.TryMatchByToolId(toolId, out var pending);
            if (matched
                && pending.LineIndex < _stream.StatusLines.Count)
            {
                // 同轮相邻同类调用并入组行（×N 递增），不再逐条平铺刷屏。
                // 组键含代理归属：不同代理的同名调用各自成组。
                var groupKey = string.IsNullOrWhiteSpace(agentName) ? name : $"{name}|{agentName}";
                if (_stream.ToolTracker.TryAppendToGroup(
                        groupKey, toolInput, result, pending.StartTick, isError,
                        out var groupLineIndex, out var members, out var totalDuration)
                    && groupLineIndex < _stream.StatusLines.Count)
                {
                    // 该 done 对应的起始行已并入组行——移除原行并校正其后行索引，
                    // 否则组行下方残留一行孤儿起始行。
                    _stream.StatusLines.RemoveAt(pending.LineIndex);
                    _stream.ToolTracker.ShiftLineIndicesFrom(pending.LineIndex, -1);
                    if (pending.LineIndex < groupLineIndex)
                        groupLineIndex--;

                    var last = members[^1];
                    _stream.StatusLines[groupLineIndex] = ConversationRenderer.MakeCompletedToolLine(
                        name, last.IsError, last.Args, $"({totalDuration})", last.Result,
                        agentName: agentName, maxWidth: ContentWidth,
                        groupCount: members.Count, group: members);
                }
                else
                {
                    _stream.ToolTracker.RegisterGroup(
                        groupKey, pending.LineIndex, toolInput, result, pending.StartTick, isError);
                    var durationStr = $"({StreamingToolTracker.FormatDuration(pending.StartTick)})";
                    // 首条成员不挂 Group：单条完成行展开仍是完整 Args/Result 详情，
                    // 第二条并入（×N ≥ 2）后才切换为组行展开形态。
                    _stream.StatusLines[pending.LineIndex] = ConversationRenderer.MakeCompletedToolLine(
                        name, isError, toolInput, durationStr, result, agentName: agentName, maxWidth: ContentWidth);
                }
            }
            // 已见过的 ToolId（ContinueStreaming 已提交到历史）— 跳过去重
            else if (!matched && _stream.ToolTracker.WasSeen(toolId))
            {
                // ToolDone for an already-committed start line — skip to avoid duplicate.
            }
            // 兜底：孤儿结果——未知 ToolId（未见起始行），或起始行已随行表压缩消失
            //（matched 但 LineIndex 越界）。不渲染即整条结果静默丢失，补为独立完成行。
            else
            {
                _stream.StatusLines.Add(ConversationRenderer.MakeCompletedToolLine(
                    name, isError, toolInput, null, result, agentName: agentName, maxWidth: ContentWidth));
            }

            // 错误摘要已内联在工具完成行（及 ×N 失败聚合行）中，展开工具行即可查看完整错误详情，
            // 不再向 StatusLines 追加多行 Notice，防止连续工具失败时产生数十行孤儿错误刷屏。

            // 工具完成即脱离"执行 X"——activity 回到通用进行中，等下一个工具/首 token 覆盖。
            // 旧实现在此不发事件：若 run 在工具后停滞/卡死，状态栏永久停留"执行 X"。
            _app.Invoke(() => ActivityChanged?.Invoke("处理中"));

            RebuildStreamingPreview();
            return;
        }

        // 非 streaming 态收到的工具完成 — 渲染为折叠的已完成工具行（含结果摘要），
        // 不再平铺完整 JSON 内容（与 streaming 态的折叠呈现保持一致）。
        // 闭包按当前视口宽度重渲（resize 时自动适配，见 AppendCommittedBlock）。
        AppendCommittedBlock(width => new[]
        {
            ConversationRenderer.MakeCompletedToolLine(name, isError, toolInput, null, result, maxWidth: width),
        });

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

        AddNoticeLines(text, color ?? TuiPalette.SystemMessage);
        RebuildStreamingPreview();
    }

    /// <summary>
    /// 把长通知文本（权限拒绝 / shell 输出预览 / 工具错误等）按视口宽度预换行为
    /// 多条通知行（CJK 显示宽度感知）。流式态的 StatusLines 不参与 resize 后的
    /// 宽度重渲，超宽单行会被终端硬裁切，必须在此处适配。
    /// </summary>
    private void AddNoticeLines(string text, Color color)
    {
        var available = Math.Max(20,
            ContentWidth - ConversationRenderer.ContentIndent
            - TextWidthHelper.GetDisplayWidth(TuiGlyphs.BorderHorizontal) - 2);
        foreach (var wrapped in ConversationRenderer.WordWrapStreaming(text, available))
        {
            if (wrapped.Length == 0) continue;
            _stream.StatusLines.Add(ConversationRenderer.MakeStreamingNotice(wrapped, color));
        }
    }

    /// <summary>
    /// 代理发言块（子代理/工作流消息）：说话人行 + 最多 3 行预览（折叠式呈现）。
    /// 预览行按视口宽度预换行；块由闭包持有宽度，resize 时整体重渲。
    /// </summary>
    public void AddAgentSpeech(string agentName, string content)
    {
        const int PreviewLineCount = 3;
        var speechLines = (content ?? string.Empty)
            .Replace("\r\n", "\n")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        AppendCommittedBlock(width =>
        {
            var available = Math.Max(10, width - ConversationRenderer.ContentIndent - 2);
            List<FormattedLine> lines = [
                FormattedLine.FromSegments(new[]
                {
                    new LineSegment($"{ConversationRenderer.Indent}", TuiPalette.BgPrimary),
                    new LineSegment($"{TuiGlyphs.Collapsed} {agentName}", TuiPalette.FgMuted),
                }),
            ];
            foreach (var line in speechLines.Take(PreviewLineCount))
                foreach (var wrapped in ConversationRenderer.WordWrapStreaming(line, available))
                    lines.Add(FormattedLine.Plain(
                        $"{ConversationRenderer.Indent}  {wrapped}", TuiPalette.FgSecondary));
            if (speechLines.Length > PreviewLineCount)
                lines.Add(FormattedLine.FromSegments(new[]
                {
                    new LineSegment($"{ConversationRenderer.Indent}", TuiPalette.BgPrimary),
                    new LineSegment($"… 共 {speechLines.Length} 行，完整内容见交付报告", TuiPalette.FgMuted),
                }));
            return lines;
        }, withSpacing: false);
    }

    public void UpdateBuildRunStatus(BuildRunStateEvent state)
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

    /// <summary>
    /// 子代理实时进度：原位重渲活跃 fork 的 Agent 工具行为 live 文案
    /// （如 "Explore · turn 2 · 5 tool calls · WebSearch…"）。无活跃行（非流式、
    /// 已完成或受控 Build attempt 降级路径）时静默忽略。
    /// </summary>
    public void UpdateSubagentProgress(SubagentProgressEvent progress)
    {
        if (!_stream.IsStreaming) return;
        if (!_stream.ToolTracker.TryGetSubagentLine(progress.AgentName, out var state)
            || state.LineIndex >= _stream.StatusLines.Count)
            return;

        // 头行保持 "Agent · [agentName] · Explore · 描述"（描述登记时固化）；其下插入/原位
        // 刷新实时活动行。进度快照写入头行 tag，展开运行中的行可见实时统计。
        // 完成/失败只清活动行——折叠行由 AddToolDone 按结果重写。
        state.LatestProgress = progress;
        _stream.StatusLines[state.LineIndex] = MessageFlowRenderer.MakeToolLine(
            "Agent", state.HeaderTarget, null, agentName: progress.AgentName,
            maxWidth: ContentWidth, liveProgress: progress);

        if (progress.Phase is SubagentProgressPhase.Completed or SubagentProgressPhase.Failed)
        {
            RemoveSubagentActivityLine(state);
            RebuildStreamingPreview();
            return;
        }

        var activity = MessageRenderer.FormatSubagentProgress(progress);
        if (state.ActivityLineIndex < 0)
        {
            var insertAt = state.LineIndex + 1;
            _stream.StatusLines.Insert(insertAt, BuildSubagentActivityLine(activity));
            // 先整体平移（本 state 的 ActivityLineIndex 仍是 -1，不会被重复平移），再登记新行。
            ShiftStreamBlockIndexesFrom(insertAt, 1);
            state.ActivityLineIndex = insertAt;
        }
        else if (state.ActivityLineIndex < _stream.StatusLines.Count)
        {
            _stream.StatusLines[state.ActivityLineIndex] = BuildSubagentActivityLine(activity);
        }
        RebuildStreamingPreview();
    }

    /// <summary>移除子代理实时活动行（完成/失败时调用）；行不存在时为 no-op。</summary>
    private void RemoveSubagentActivityLine(StreamingToolTracker.SubagentLineState state)
    {
        if (state.ActivityLineIndex < 0) return;
        if (state.ActivityLineIndex < _stream.StatusLines.Count)
        {
            _stream.StatusLines.RemoveAt(state.ActivityLineIndex);
            ShiftStreamBlockIndexesFrom(state.ActivityLineIndex, -1);
        }
        state.ActivityLineIndex = -1;
    }

    private FormattedLine BuildSubagentActivityLine(string text) =>
        FormattedLine.FromSegments(
        [
            new LineSegment($"{ConversationRenderer.Indent}  ", TuiPalette.BgPrimary),
            new LineSegment(text, TuiPalette.FgMuted),
        ]);

    /// <summary>状态行插入/删除后平移流内所有行索引登记（块状态行、思考行、工具行、子代理行）。</summary>
    private void ShiftStreamBlockIndexesFrom(int fromIndex, int delta)
    {
        if (delta == 0) return;
        if (_stream.BuildRunStatusLineIndex >= fromIndex) _stream.BuildRunStatusLineIndex += delta;
        if (_stream.ModeProgressLineIndex >= fromIndex) _stream.ModeProgressLineIndex += delta;
        if (_stream.ThinkingSummaryLineIndex >= fromIndex) _stream.ThinkingSummaryLineIndex += delta;
        _stream.ToolTracker.ShiftLineIndicesFrom(fromIndex, delta);
    }

    public void AddBuildDeliveryCard(OneCode.Core.Build.BuildRunResult result)
    {
        // BuildRunCompletedEvent 在流收尾阶段先于 WorkflowRunEndedEvent 到达；
        // 若塞入 _stream.StatusLines，不仅会被 BuildCommittedStreamLines 逐行插入空行，
        // 还会排在末轮助手回复的正文上方。先结束当前流提交末轮正文，再将交付卡作为独立块追加在末尾。
        if (_stream.IsStreaming)
            EndStreaming();
        AppendCommittedBlock(
            width => ChatBlockRenderers.RenderBuildDeliveryCard(result, width),
            withSpacing: false);
    }

    public void UpdateModeProgress(ModeProgressEvent progress)
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
    /// EditTransactionMiddleware 检测到 Write/Edit 后发射 FileChangeEvent 事件，
    /// 此方法渲染 +N/-M 行的 Diff 块。Diff 行按内容宽度预换行；
    /// 提交路径经日志条目持有宽度闭包，宽度变化时整体重渲。
    /// </summary>
    public void AddFileChange(string fileName, IReadOnlyList<string> addedLines, IReadOnlyList<string> removedLines)
        => AddFileChange(fileName, null, addedLines, removedLines);

    /// <summary>带代理归属的重载：diff 头标注执行修改的代理 ID。</summary>
    public void AddFileChange(string fileName, string? agentName, IReadOnlyList<string> addedLines, IReadOnlyList<string> removedLines)
    {
        _logger.LogDebug(
            "[AddFileChange] streaming={Streaming}, statusLines={StatusLines}, lineCountInView={LineCountInView}, file={File}, +{Added}/-{Removed}, agent={Agent}",
            _stream.IsStreaming, _stream.StatusLines.Count, _stream.PreviewLineCount, fileName, addedLines.Count, removedLines.Count, agentName);
        if (_stream.IsStreaming)
        {
            AddFormattedLines(ChatBlockRenderers.RenderDiffBlock(
                fileName, addedLines, removedLines,
                addedSummary: addedLines.Count,
                removedSummary: removedLines.Count,
                agentName: agentName,
                viewWidth: ContentWidth));
        }
        else
        {
            AppendCommittedBlock(width => ChatBlockRenderers.RenderDiffBlock(
                fileName, addedLines, removedLines,
                addedSummary: addedLines.Count,
                removedSummary: removedLines.Count,
                agentName: agentName,
                viewWidth: width));
        }
    }

    /// <summary>流式期间追加状态行（宽度相关块由提交时的日志条目负责重渲）。</summary>
    private void AddFormattedLines(IReadOnlyList<FormattedLine> lines)
    {
        foreach (var line in lines)
            _stream.StatusLines.Add(line);
        RebuildStreamingPreview();
    }
}
