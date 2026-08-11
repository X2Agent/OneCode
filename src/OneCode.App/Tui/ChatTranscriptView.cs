using System.Text.RegularExpressions;

namespace OneCode.App.Tui;

public sealed partial class ChatTranscriptView : View
{
    private readonly IApplication _app;
    private readonly MessageListView _messageView;
    private readonly MessageFlowRenderer _renderer;
    private readonly ILogger<ChatTranscriptView> _logger;
    private readonly StreamingTranscriptState _stream = new();

    private int _inputTokens;
    private int _outputTokens;
    private int _turnNumber;
    private int _toolCount;

    // Stored welcome info so we can re-render on resize (centering depends on width).
    private WelcomeInfo? _welcomeInfo;
    private bool _isWelcomeShowing;

    private int _lastViewportWidth;
    private int _lastViewportHeight;

    public MessageListView MessageView => _messageView;

    /// <summary>
    /// Content column width for wrapping — tracks the live viewport so chat
    /// fills available horizontal space.
    /// </summary>
    private int ContentWidth => TuiSpacing.GetContentColumnWidth(Viewport.Width);

    public WorkingMode CurrentMode
    {
        get => _renderer.CurrentMode;
        set => _renderer.CurrentMode = value;
    }

    /// <summary>Raised when the agent runtime phase changes.</summary>
    public event Action<string>? ActivityChanged;

    public ChatTranscriptView(IApplication app, OneCode.Core.IO.IClipboardService? clipboard = null, Func<bool>? getShowThinking = null, ILogger<ChatTranscriptView>? logger = null)
    {
        _app = app;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<ChatTranscriptView>.Instance;
        _renderer = new MessageFlowRenderer();
        _renderer.GetShowThinking = getShowThinking;

        _messageView = new MessageListView(clipboard);

        Width = Dim.Fill();
        Height = Dim.Fill();
        SetScheme(TuiTheme.ConversationArea);

        Add(_messageView);

        _lastViewportWidth = Viewport.Width;
        _lastViewportHeight = Viewport.Height;
    }

    public void Clear()
    {
        CancelPendingStreamingRebuild();
        _stream.Clear();
        _inputTokens = 0;
        _outputTokens = 0;
        _turnNumber = 0;
        _toolCount = 0;
        _isWelcomeShowing = false;
        _messageView.Clear();
    }

    /// <summary>Show welcome screen with full product info on startup or after conversation reset.</summary>
    public void ShowWelcome(WelcomeInfo info)
    {
        _welcomeInfo = info;
        _isWelcomeShowing = true;
        RenderWelcome();
    }

    /// <summary>
    /// Renders the stored welcome info using the current viewport width.
    /// Called on initial display and on resize.
    /// </summary>
    private void RenderWelcome()
    {
        if (_welcomeInfo is null) return;
        // Defer until layout has a real width so welcome isn't baked at the fallback size.
        if (Viewport.Width <= 0) return;

        var w = Math.Max(40, ContentWidth);
        var h = Math.Max(0, Viewport.Height);
        _renderer.CurrentWidth = w;
        _messageView.Clear();
        var lines = WelcomeRenderer.Render(_welcomeInfo, w, h);
        _messageView.AppendLines(lines);
    }

    public void AddUserMessage(string text)
    {
        _app.Invoke(() => AddUserMessageDirect(text));
    }

    /// <summary>
    /// Renders a user message directly without dispatching to the UI thread.
    /// Call this only when already on the UI thread (e.g., from event handlers
    /// like OnUserSubmitted). This avoids the nested-Invoke delay where an outer
    /// Invoke posts to the UI queue, and then AddUserMessage's internal Invoke
    /// posts again, deferring the actual render to a later queue drain cycle.
    /// </summary>
    /// <summary>
    /// Appends lines to the message view with a blank-line separator before
    /// the new content (if the view already has content). This provides
    /// uniform spacing between all message types without each renderer
    /// needing to manage its own leading/trailing blank lines.
    /// </summary>
    private void AppendWithSpacing(IReadOnlyList<FormattedLine> lines)
    {
        if (_messageView.TotalLines > 0)
            _messageView.AppendLine("", TuiPalette.BgPrimary);
        _messageView.AppendLines(lines);
    }

    public void AddUserMessageDirect(string text)
    {
        // DESIGN.md: welcome is replaced by conversation content on first message.
        if (_isWelcomeShowing)
        {
            _messageView.Clear();
            _isWelcomeShowing = false;
        }

        _renderer.CurrentWidth = ContentWidth;
        var lines = _renderer.RenderMessage(new UserMessage(
            Id: Guid.NewGuid().ToString("N"),
            Content: text,
            Timestamp: DateTimeOffset.UtcNow));
        InvalidateTrailingModeBanner();
        AppendWithSpacing(lines);
        _stream.TextBuffer.Clear();
    }

    public void AddSystem(string text)
    {
        _renderer.CurrentWidth = ContentWidth;
        var lines = _renderer.RenderMessage(new SystemMessage(
            Id: Guid.NewGuid().ToString("N"),
            Content: text,
            Timestamp: DateTimeOffset.UtcNow));
        InvalidateTrailingModeBanner();
        AppendWithSpacing(lines);
    }

    // Search state — saved across calls to support /find next
    private string? _lastSearchQuery;
    private bool _lastSearchIsRegex;
    private int _lastSearchMatchIdx;

    /// <summary>
    /// 搜索会话中包含指定文本的行，并滚动到第一个匹配项。
    /// 返回 (匹配总数, 当前匹配索引)。无匹配时返回 (0, -1)。
    /// </summary>
    public (int TotalMatches, int CurrentIndex) SearchAndScroll(string query, int startFrom = 0)
    {
        var matches = _messageView.FindMatches(query);
        if (matches.Count == 0)
        {
            _messageView.SetSearchHighlight(null, null);
            return (0, -1);
        }

        // 找到 startFrom 之后的第一个匹配
        var idx = matches.FindIndex(m => m >= startFrom);
        if (idx < 0) idx = 0; // 回绕到第一个
        var targetLine = matches[idx];

        // 高亮所有匹配行中的关键词
        _messageView.SetSearchHighlight(query, matches);
        _messageView.ScrollToLine(targetLine);

        // 保存搜索状态供 /find next 使用
        _lastSearchQuery = query;
        _lastSearchIsRegex = false;
        _lastSearchMatchIdx = idx;

        return (matches.Count, idx);
    }

    /// <summary>
    /// 使用正则表达式搜索会话中的匹配行。
    /// </summary>
    public (int TotalMatches, int CurrentIndex) SearchAndScrollRegex(string pattern, int startFrom = 0)
    {
        var matches = _messageView.FindMatchesRegex(pattern, out var regex);
        if (matches.Count == 0)
        {
            _messageView.SetSearchHighlight(null, null);
            return (0, -1);
        }

        var idx = matches.FindIndex(m => m >= startFrom);
        if (idx < 0) idx = 0;
        var targetLine = matches[idx];

        _messageView.SetSearchHighlight(pattern, matches, compiledRegex: regex);
        _messageView.ScrollToLine(targetLine);

        _lastSearchQuery = pattern;
        _lastSearchIsRegex = true;
        _lastSearchMatchIdx = idx;

        return (matches.Count, idx);
    }

    /// <summary>
    /// 继续搜索上次的关键词，跳转到下一个匹配项。
    /// 返回 (匹配总数, 当前匹配索引)。无上次搜索或无匹配时返回 (0, -1)。
    /// </summary>
    public (int TotalMatches, int CurrentIndex) FindNext()
    {
        if (string.IsNullOrEmpty(_lastSearchQuery))
            return (0, -1);

        Regex? regex = null;
        var matches = _lastSearchIsRegex
            ? _messageView.FindMatchesRegex(_lastSearchQuery, out regex)
            : _messageView.FindMatches(_lastSearchQuery);
        if (matches.Count == 0)
            return (0, -1);

        // 跳到下一个匹配（回绕到第一个）
        var nextIdx = (_lastSearchMatchIdx + 1) % matches.Count;
        var targetLine = matches[nextIdx];
        _messageView.SetSearchHighlight(
            _lastSearchQuery, matches, compiledRegex: regex);
        _messageView.ScrollToLine(targetLine);
        _lastSearchMatchIdx = nextIdx;

        return (matches.Count, nextIdx);
    }

    /// <summary>清除搜索高亮。</summary>
    public void ClearSearchHighlight()
    {
        _messageView.SetSearchHighlight(null, null);
    }

    public void AddError(string text)
    {
        _app.Invoke(() =>
        {
            _renderer.CurrentWidth = ContentWidth;
            _stream.TextBuffer.Clear();
            _stream.IsStreaming = false;
            _stream.PendingLines = null;
            _stream.StatusLines.Clear();
            _stream.BuildRunStatusLineIndex = -1;
            _stream.BuildRunStatusLineCount = 0;
            _stream.ModeProgressLineIndex = -1;
            _stream.ModeProgressLineCount = 0;
            _stream.ToolTracker.Clear();

            var maxWidth = Math.Max(20, ContentWidth - 6);
            InvalidateTrailingModeBanner();

            var errorLines = text.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
            var firstLine = errorLines[0];
            var summary = firstLine.Length > maxWidth - 6
                ? firstLine[..(maxWidth - 6)] + TuiGlyphs.Ellipsis
                : firstLine;
            var tag = new ErrorLineTag(text, IsExpanded: true);
            var errorHeaderLines = new List<FormattedLine>
            {
                FormattedLine.FromSegmentsWithTag(new[]
                {
                    new LineSegment($" {TuiGlyphs.Expanded} {TuiGlyphs.Failed}  ", TuiPalette.Error),
                    new LineSegment(summary, TuiPalette.Error),
                }, tag),
            };

            if (errorLines.Length > 1)
            {
                var maxContentWidth = Math.Max(20, ContentWidth - ConversationRenderer.ContentIndent - 2);
                var startIdx = firstLine.Length <= maxWidth - 6 ? 1 : 0;
                for (var li = startIdx; li < errorLines.Length; li++)
                {
                    var line = errorLines[li];
                    if (string.IsNullOrEmpty(line))
                    {
                        errorHeaderLines.Add(FormattedLine.Plain(ConversationRenderer.Indent, TuiPalette.Error));
                        continue;
                    }
                    foreach (var w in TextWidthHelper.WordWrapByWidth(line, maxContentWidth))
                        errorHeaderLines.Add(FormattedLine.Plain($"{ConversationRenderer.Indent}  {w}", TuiPalette.Error));
                }
            }
            AppendWithSpacing(errorHeaderLines);
        });
    }

    public void AddCommandResult(string text)
    {
        _renderer.CurrentWidth = ContentWidth;
        var lines = _renderer.RenderMessage(new SystemMessage(
            Id: Guid.NewGuid().ToString("N"),
            Content: text,
            Timestamp: DateTimeOffset.UtcNow));
        InvalidateTrailingModeBanner();
        AppendWithSpacing(lines);
    }

    /// <summary>
    /// Updates the live mode banner so it always matches the AgentStatusBar mode tag.
    /// Rapid Tab replaces the previous trailing banner in-place instead of stacking
    /// snapshots that lag one step behind the live controller state.
    /// </summary>
    public void UpdateModeBanner(IReadOnlyList<FormattedLine> bannerLines)
    {
        if (_stream.IsStreaming && _stream.PreviewLineCount > 0)
        {
            if (_stream.TrailingModeBannerLineCount > 0)
            {
                var removeAt = _messageView.TotalLines - _stream.PreviewLineCount - _stream.TrailingModeBannerLineCount;
                _messageView.RemoveRange(removeAt, _stream.TrailingModeBannerLineCount);
            }
            _messageView.InsertBeforeLast(_stream.PreviewLineCount, bannerLines);
            _stream.TrailingModeBannerLineCount = bannerLines.Count;
            return;
        }

        if (_stream.TrailingModeBannerLineCount > 0)
            _messageView.ReplaceLastLines(_stream.TrailingModeBannerLineCount, bannerLines);
        else
            _messageView.AppendLines(bannerLines);
        _stream.TrailingModeBannerLineCount = bannerLines.Count;
    }

    /// <summary>
    /// Inserts pre-rendered banner/card lines (e.g. historical plan cards) into the chat.
    /// Unlike <see cref="UpdateModeBanner"/>, this always appends and seals any
    /// trailing mode banner so later mode switches start a new history entry.
    /// </summary>
    public void AddModeBanner(IReadOnlyList<FormattedLine> bannerLines)
    {
        InvalidateTrailingModeBanner();
        if (_stream.IsStreaming && _stream.PreviewLineCount > 0)
            _messageView.InsertBeforeLast(_stream.PreviewLineCount, bannerLines);
        else
            _messageView.AppendLines(bannerLines);
    }

    /// <summary>Displays one active plan card and replaces it in place as its phase changes.</summary>
    public void UpdatePlanCard(IReadOnlyList<FormattedLine> cardLines)
    {
        InvalidateTrailingModeBanner();
        if (_stream.ActivePlanCardLineIndex >= 0)
        {
            _messageView.ReplaceRange(_stream.ActivePlanCardLineIndex, _stream.ActivePlanCardLineCount, cardLines);
            _stream.ActivePlanCardLineCount = cardLines.Count;
            return;
        }

        _stream.ActivePlanCardLineIndex = Math.Max(0, _messageView.TotalLines - (_stream.IsStreaming ? _stream.PreviewLineCount : 0));
        if (_stream.IsStreaming && _stream.PreviewLineCount > 0)
            _messageView.InsertBeforeLast(_stream.PreviewLineCount, cardLines);
        else
            _messageView.AppendLines(cardLines);
        _stream.ActivePlanCardLineCount = cardLines.Count;
    }

    public void ClearActivePlanCard()
    {
        _stream.ActivePlanCardLineIndex = -1;
        _stream.ActivePlanCardLineCount = 0;
    }

    private void InvalidateTrailingModeBanner() => _stream.TrailingModeBannerLineCount = 0;

    public void BeginStreaming()
    {
        _stream.ResetForNewStream(new AssistantMessage(
            Id: Guid.NewGuid().ToString("N"),
            Content: new List<ContentBlock>(),
            Timestamp: DateTimeOffset.UtcNow));

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

        if (_stream.PendingLines is { Count: > 0 } && _stream.PreviewLineCount > 0)
        {
            var committedLines = BuildFinalCommittedLines();
            _messageView.ReplaceLastLines(_stream.PreviewLineCount, committedLines);
        }

        _stream.ResetForNextTurn();

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
            var finalLines = BuildFinalCommittedLines();
            _messageView.ReplaceLastLines(_stream.PreviewLineCount, finalLines);
        }
        else if (_stream.StatusLines.Count > 0)
        {
            // Safeguard: if _stream.PendingLines is null (e.g., EndStreaming was
            // already called from an error handler) but _stream.StatusLines
            // still has uncommitted items (e.g., a TuiFileChange that arrived
            // after the first EndStreaming), append them directly so they are
            // not lost.
            _renderer.CurrentWidth = ContentWidth;
            _messageView.AppendLines(_stream.StatusLines);
        }

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
            RebuildStreamingPreview(0);
        }
        else
        {
            ScheduleStreamingRebuild();
        }
    }

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

    public void LoadConversation(Conversation conversation)
    {
        Clear();

        _renderer.CurrentWidth = ContentWidth;

        foreach (var msg in conversation.Messages)
        {
            var lines = _renderer.RenderMessage(msg);
            AppendWithSpacing(lines);
        }

        _messageView.ScrollToBottom();
    }

    public (int InputTokens, int OutputTokens) GetTokenUsage()
        => (_inputTokens, _outputTokens);

    public int GetTurnNumber() => _turnNumber;
    public int GetToolCount() => _toolCount;

    public void NotifyLayoutChanged()
    {
        _renderer.CurrentWidth = ContentWidth;
    }

    public void SetShowThinking(bool show)
    {
        _renderer.ShowThinking = show;
    }

    public void HandleResize(int newWidth, int newHeight)
    {
        if (newWidth == _lastViewportWidth && newHeight == _lastViewportHeight)
            return;

        _lastViewportWidth = newWidth;
        _lastViewportHeight = newHeight;

        _renderer.CurrentWidth = TuiSpacing.GetContentColumnWidth(newWidth);
        _messageView.ReflowExpandedToolDetails(newWidth);

        // Re-render welcome screen so logo / text re-center in the new width
        if (_isWelcomeShowing)
            RenderWelcome();

        _messageView.SetNeedsDraw();
        SetNeedsDraw();
    }

    /// <summary>
    /// Terminal.Gui calls this on every draw cycle. We detect viewport size changes
    /// (e.g., terminal resize, fullscreen toggle) and re-render the welcome screen
    /// so welcome content re-wraps / re-centers for the new width.
    ///
    /// NOTE: child layout (X/Y/Width/Height on _messageView) is set ONCE in the
    /// constructor — repeating it here caused Terminal.Gui to invalidate layout
    /// on every draw pass, producing visible flicker and wasted CPU.
    /// </summary>
    protected override bool OnDrawingContent(DrawContext? context)
    {
        // Detect resize: if viewport dimensions changed since last draw, re-render.
        var vp = Viewport;
        if (vp.Width != _lastViewportWidth || vp.Height != _lastViewportHeight)
        {
            HandleResize(vp.Width, vp.Height);
        }

        // Keep the renderer's width in sync without touching child layout.
        _renderer.CurrentWidth = TuiSpacing.GetContentColumnWidth(vp.Width);
        return base.OnDrawingContent(context);
    }

    // Streaming preview rebuild is debounced via this tick. Token arrivals can
    // fire hundreds of times per second for fast models; re-wrapping the entire
    // _stream.TextBuffer on every token makes streaming O(n²) in response length
    // and causes visible lag on long answers. We coalesce token bursts into a
    // single rebuild on the next UI-loop iteration.
    private const int StreamingRebuildCadenceMs = 16; // ~60 FPS cap

    private void RebuildStreamingPreview(int previousLineCount)
    {
        _renderer.CurrentWidth = ContentWidth;
        _stream.PendingLines = new List<FormattedLine>();

        for (var i = 0; i < _stream.StatusLines.Count; i++)
        {
            _stream.PendingLines.Add(_stream.StatusLines[i]);
            if (i < _stream.StatusLines.Count - 1)
                _stream.PendingLines.Add(FormattedLine.Plain("", TuiPalette.BgPrimary));
        }

        // Incremental wrap: cache completed paragraphs; only re-wrap the trailing
        // incomplete paragraph (and any newly completed ones) on each rebuild.
        var fullText = _stream.TextBuffer.ToString();
        var wrapWidth = _renderer.CurrentWidth - ConversationRenderer.ContentIndent - 2;
        UpdateStreamingWrapCache(fullText, wrapWidth);

        if (_stream.WrappedTextLines.Count > 0 && _stream.PendingLines.Count > 0)
            _stream.PendingLines.Add(FormattedLine.Plain("", TuiPalette.BgPrimary));
        foreach (var line in _stream.WrappedTextLines)
        {
            _stream.PendingLines.Add(FormattedLine.Plain($"{ConversationRenderer.Indent}{line}", TuiPalette.AssistantMessage));
        }

        _stream.PreviewLineCount = _stream.PendingLines.Count;
        _messageView.ReplaceLastLines(previousLineCount, _stream.PendingLines);
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
                RebuildStreamingPreview(_stream.PreviewLineCount);
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
    {
        var lines = new List<FormattedLine>();

        // Preserve tool-call / thinking / notice lines with uniform spacing.
        foreach (var line in _stream.StatusLines)
        {
            lines.Add(line);
            lines.Add(FormattedLine.Plain("", TuiPalette.BgPrimary));
        }
        if (_stream.StatusLines.Count > 0 && lines.Count > 0)
            lines.RemoveAt(lines.Count - 1);

        // Re-render the accumulated text through the Markdown renderer.
        var text = _stream.TextBuffer.ToString();
        if (!string.IsNullOrWhiteSpace(text))
        {
            if (lines.Count > 0)
                lines.Add(FormattedLine.Plain("", TuiPalette.BgPrimary));
            _renderer.AppendAssistantText(lines, text);
        }

        return lines;
    }
}
