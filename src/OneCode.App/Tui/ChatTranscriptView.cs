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

}
