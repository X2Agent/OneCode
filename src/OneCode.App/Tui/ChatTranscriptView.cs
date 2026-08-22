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

    // —— 已提交内容重渲日志 ——
    // 对话行在追加时按当时宽度换行，宽度变化（侧边栏拖拽/开关、终端 resize）
    // 后旧行不再匹配视口。每个已提交块在此登记按宽度重渲的闭包，
    // RequestContentRerender → 下一次绘制时按新宽度整体重渲（见类头注释）。
    private readonly List<Func<int, IReadOnlyList<FormattedLine>>> _committedJournal = [];
    private int _trailingBannerJournalIndex = -1;
    private bool _contentRerenderRequested;

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
        _committedJournal.Clear();
        _trailingBannerJournalIndex = -1;
        _contentRerenderRequested = false;
    }

    /// <summary>Show welcome screen with full product info on startup or after conversation reset.</summary>
    public void ShowWelcome(WelcomeInfo info)
    {
        _welcomeInfo = info;
        _isWelcomeShowing = true;
        _committedJournal.Clear();
        _trailingBannerJournalIndex = -1;
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
    public void AddUserMessageDirect(string text)
    {
        // DESIGN.md: welcome is replaced by conversation content on first message.
        if (_isWelcomeShowing)
        {
            _messageView.Clear();
            _committedJournal.Clear();
            _trailingBannerJournalIndex = -1;
            _isWelcomeShowing = false;
        }

        InvalidateTrailingModeBanner();
        AppendCommittedBlock(RenderMessageJournalEntry(new UserMessage(
            Id: Guid.NewGuid().ToString("N"),
            Content: text,
            Timestamp: DateTimeOffset.UtcNow)));
        _stream.TextBuffer.Clear();
    }

    public void AddSystem(string text)
    {
        InvalidateTrailingModeBanner();
        AppendCommittedBlock(RenderMessageJournalEntry(new SystemMessage(
            Id: Guid.NewGuid().ToString("N"),
            Content: text,
            Timestamp: DateTimeOffset.UtcNow)));
    }

    // Search: /find and /find next live in ChatTranscriptView.Search.cs

    public void AddError(string text)
    {
        _app.Invoke(() =>
        {
            _stream.TextBuffer.Clear();
            _stream.IsStreaming = false;
            _stream.PendingLines = null;
            _stream.StatusLines.Clear();
            _stream.BuildRunStatusLineIndex = -1;
            _stream.BuildRunStatusLineCount = 0;
            _stream.ModeProgressLineIndex = -1;
            _stream.ModeProgressLineCount = 0;
            _stream.ToolTracker.Clear();

            InvalidateTrailingModeBanner();
            AppendCommittedBlock(width => RenderErrorBlock(text, width));
        });
    }

    /// <summary>
    /// 构建错误块（首行摘要 + 展开的详情行）。按 <paramref name="contentWidth"/>
    /// 换行/截断，供追加与宽度变化后的整体重渲共用。
    /// </summary>
    private static IReadOnlyList<FormattedLine> RenderErrorBlock(string text, int contentWidth)
    {
        var maxWidth = Math.Max(20, contentWidth - 6);
        var errorLines = text.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
        var firstLine = errorLines[0];
        var summary = firstLine.Length > maxWidth - 6
            ? firstLine[..(maxWidth - 6)] + TuiGlyphs.Ellipsis
            : firstLine;
        var tag = new ErrorLineTag(text, IsExpanded: true);
        var lines = new List<FormattedLine>
        {
            FormattedLine.FromSegmentsWithTag(new[]
            {
                new LineSegment($" {TuiGlyphs.Expanded} {TuiGlyphs.Failed}  ", TuiPalette.Error),
                new LineSegment(summary, TuiPalette.Error),
            }, tag),
        };

        if (errorLines.Length > 1)
        {
            var maxContentWidth = Math.Max(20, contentWidth - ConversationRenderer.ContentIndent - 2);
            var startIdx = firstLine.Length <= maxWidth - 6 ? 1 : 0;
            for (var li = startIdx; li < errorLines.Length; li++)
            {
                var line = errorLines[li];
                if (string.IsNullOrEmpty(line))
                {
                    lines.Add(FormattedLine.Plain(ConversationRenderer.Indent, TuiPalette.Error));
                    continue;
                }
                foreach (var w in TextWidthHelper.WordWrapByWidth(line, maxContentWidth))
                    lines.Add(FormattedLine.Plain($"{ConversationRenderer.Indent}  {w}", TuiPalette.Error));
            }
        }
        return lines;
    }

    public void AddCommandResult(string text)
    {
        InvalidateTrailingModeBanner();
        AppendCommittedBlock(RenderMessageJournalEntry(new SystemMessage(
            Id: Guid.NewGuid().ToString("N"),
            Content: text,
            Timestamp: DateTimeOffset.UtcNow)));
    }

    /// <summary>
    /// Updates the live mode banner so it always matches the AgentStatusBar mode tag.
    /// Rapid Tab replaces the previous trailing banner in-place instead of stacking
    /// snapshots that lag one step behind the live controller state.
    /// </summary>
    public void UpdateModeBanner(IReadOnlyList<FormattedLine> bannerLines)
    {
        var previewCount = _messageView.StreamingPreviewLineCount;
        if (previewCount == 0)
            previewCount = _stream.PreviewLineCount;
        if (_stream.IsStreaming && previewCount > 0)
        {
            if (_stream.TrailingModeBannerLineCount > 0)
            {
                var removeAt = _messageView.TotalLines - previewCount - _stream.TrailingModeBannerLineCount;
                _messageView.RemoveRange(removeAt, _stream.TrailingModeBannerLineCount);
            }
            _messageView.InsertBeforeLast(previewCount, bannerLines);
            _stream.TrailingModeBannerLineCount = bannerLines.Count;
            SetTrailingModeBannerJournal(bannerLines);
            return;
        }

        if (_stream.TrailingModeBannerLineCount > 0)
            _messageView.ReplaceLastLines(_stream.TrailingModeBannerLineCount, bannerLines);
        else
            _messageView.AppendLines(bannerLines);
        _stream.TrailingModeBannerLineCount = bannerLines.Count;
        SetTrailingModeBannerJournal(bannerLines);
    }

    /// <summary>
    /// Inserts pre-rendered banner/card lines (e.g. historical plan cards) into the chat.
    /// Unlike <see cref="UpdateModeBanner"/>, this always appends and seals any
    /// trailing mode banner so later mode switches start a new history entry.
    /// </summary>
    public void AddModeBanner(IReadOnlyList<FormattedLine> bannerLines)
    {
        InvalidateTrailingModeBanner();
        var previewCount = _messageView.StreamingPreviewLineCount > 0
            ? _messageView.StreamingPreviewLineCount
            : _stream.PreviewLineCount;
        if (_stream.IsStreaming && previewCount > 0)
            _messageView.InsertBeforeLast(previewCount, bannerLines);
        else
            _messageView.AppendLines(bannerLines);
        // 历史 banner 与宽度无关：作为静态条目追加，且不占用"尾部可替换"槽位
        // （后续 UpdateModeBanner 追加新条目而非替换本条）。
        var snapshot = bannerLines.ToList();
        _committedJournal.Add(_ => snapshot);
    }

    private void InvalidateTrailingModeBanner()
    {
        _stream.TrailingModeBannerLineCount = 0;
        // banner 行本身保留在日志中，只是不再被视为可整体替换的尾部 banner。
        _trailingBannerJournalIndex = -1;
    }

    /// <summary>
    /// 同步尾部 banner 到重渲日志：已有尾部 banner 条目则原位替换（对应
    /// <see cref="MessageListView.ReplaceLastLines"/> 的视图语义），否则追加。
    /// banner 与宽度无关，日志条目直接持有渲染结果。
    /// </summary>
    private void SetTrailingModeBannerJournal(IReadOnlyList<FormattedLine> bannerLines)
    {
        var snapshot = bannerLines.ToList();
        if (_trailingBannerJournalIndex is >= 0 and var idx && idx < _committedJournal.Count)
        {
            _committedJournal[idx] = _ => snapshot;
            return;
        }
        _committedJournal.Add(_ => snapshot);
        _trailingBannerJournalIndex = _committedJournal.Count - 1;
    }

    public void LoadConversation(Conversation conversation)
    {
        Clear();

        foreach (var msg in conversation.Messages)
            AppendCommittedBlock(RenderMessageJournalEntry(msg));

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

    // —— 已提交内容重渲 ——
    // 触发链：ReplShell（侧边栏拖拽结束 / 侧边栏开关 / 终端 resize）调用
    // RequestContentRerender 置位；下一次绘制时布局已按新宽度解析，此时
    // 按 ContentWidth 重放日志整体重渲（绘制期改写消息行与 RenderWelcome
    // 的既有模式一致，随后子视图在同一绘制遍中完成绘制）。

    /// <summary>
    /// 请求按当前视口宽度整体重渲对话内容。宽度变化（Plan 侧边栏拖拽/开关、
    /// 终端 resize）后旧行换行不再匹配视口——右侧留白或文字被截断。
    /// 实际重渲延迟到下一次绘制，确保布局先行解析出新宽度。
    /// </summary>
    public void RequestContentRerender()
    {
        _contentRerenderRequested = true;
        SetNeedsDraw();
    }

    /// <summary>
    /// 按指定宽度重放全部已提交块并重建消息行。流式预览窗口按新宽度重建，
    /// 尾部交互区域与展开/滚动状态由 <see cref="MessageListView.RerenderAll"/> 保留。
    /// </summary>
    internal void RerenderCommittedContent(int width)
    {
        if (_isWelcomeShowing)
        {
            RenderWelcome();
            return;
        }

        List<FormattedLine>? previewLines = null;
        if (_stream.IsStreaming)
        {
            previewLines = BuildStreamingPreviewLines(width);
            _stream.PendingLines = previewLines;
        }

        _messageView.RerenderAll(() => BuildCommittedLinesAt(width), previewLines);

        if (_stream.IsStreaming)
            _stream.PreviewLineCount = _messageView.StreamingPreviewLineCount;
    }

    private IReadOnlyList<FormattedLine> BuildCommittedLinesAt(int width)
    {
        if (_committedJournal.Count == 0)
            return [];

        var all = new List<FormattedLine>(_committedJournal.Count * 8);
        foreach (var entry in _committedJournal)
            all.AddRange(entry(width));
        return all;
    }

    /// <summary>
    /// 追加一个已提交块：先按当前宽度渲染进消息视图，同时登记按宽度重渲的
    /// 闭包。<paramref name="withSpacing"/> 为 true 时按 AppendWithSpacing
    /// 语义在视图已有内容时前置空行；间距在追加时确定并固化进日志条目——
    /// 重放顺序与原顺序一致，间距因此稳定。
    /// </summary>
    private void AppendCommittedBlock(
        Func<int, IReadOnlyList<FormattedLine>> renderAtWidth,
        bool withSpacing = true)
    {
        var spacing = withSpacing && _messageView.TotalLines > 0;
        if (spacing)
        {
            _committedJournal.Add(width =>
            {
                var lines = new List<FormattedLine> { FormattedLine.Plain("", TuiPalette.BgPrimary) };
                lines.AddRange(renderAtWidth(width));
                return lines;
            });
        }
        else
        {
            _committedJournal.Add(renderAtWidth);
        }

        var current = renderAtWidth(ContentWidth);
        if (spacing)
            _messageView.AppendLine("", TuiPalette.BgPrimary);
        _messageView.AppendLines(current);
    }

    /// <summary>
    /// 为可由 <see cref="MessageFlowRenderer"/> 重渲的消息构造日志闭包。
    /// 闭包捕获追加时的模式（影响用户消息竖线配色），重放时临时借用共享
    /// renderer 渲染并恢复其状态——避免重放篡改当前模式/宽度。
    /// </summary>
    private Func<int, IReadOnlyList<FormattedLine>> RenderMessageJournalEntry(Message message)
    {
        var mode = _renderer.CurrentMode;
        return width =>
        {
            var (prevMode, prevWidth) = (_renderer.CurrentMode, _renderer.CurrentWidth);
            _renderer.CurrentMode = mode;
            _renderer.CurrentWidth = width;
            try
            {
                return _renderer.RenderMessage(message);
            }
            finally
            {
                _renderer.CurrentMode = prevMode;
                _renderer.CurrentWidth = prevWidth;
            }
        };
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

        // 宽度变化触发的整体重渲在绘制期消费：此刻布局已解析出最终视口宽度。
        if (_contentRerenderRequested)
        {
            _contentRerenderRequested = false;
            RerenderCommittedContent(ContentWidth);
        }

        // Keep the renderer's width in sync without touching child layout.
        _renderer.CurrentWidth = TuiSpacing.GetContentColumnWidth(vp.Width);
        return base.OnDrawingContent(context);
    }

}
