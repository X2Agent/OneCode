namespace OneCode.App.Tui;

/// <summary>
/// Right-docked sidebar base — shared chrome for the Plan and Team side panels.
///
/// Provides: fixed header line (glyph + title, never scrolls away), scrollable
/// content area (reuses <see cref="MessageListView"/>), left separator line as a
/// drag handle for width adjustment, and width clamping to [MinWidth, ComputeMaxWidth(screenWidth)].
///
/// Display-only by design: <c>CanFocus = false</c>, no keyboard handling — all
/// user interaction happens in the main conversation column.
/// </summary>
internal abstract class SidebarViewBase : View
{
    public const int DefaultWidth = 35;
    public const int MinWidth = 28;

    /// <summary>分隔线拖动手柄的宽度（列）。未处理鼠标事件会冒泡到本视图，
    /// 因此手柄区可覆盖分隔线列及紧邻的一列内容列，便于抓取。</summary>
    private const int DragHandleWidth = 2;

    /// <summary>拖动调整宽度时给对话区保留的最小列数，防止面板挤占整个屏幕。</summary>
    public const int ChatColumnMinWidth = TuiSpacing.ChatColumnMinWidth;

    private readonly IApplication _app;
    private readonly MessageListView _content;
    private readonly Action _widthChanged;
    private readonly Action _dragEnded;
    private readonly string _defaultTitle;
    private string _headerTitle;
    private bool _isDragging;
    private bool _isHovering;

    /// <summary>Current panel width in columns (mutable via separator drag).</summary>
    public int CurrentWidth { get; private set; } = DefaultWidth;

    /// <summary>标题行前缀字形（如 📋 / 🛠），由具体面板指定。</summary>
    protected abstract string HeaderGlyph { get; }

    /// <summary>标题行属性（Plan/Team 各用模式主题色，与对话流卡片头一致）。</summary>
    protected abstract Attribute HeaderAttribute { get; }

    /// <param name="app">宿主 Application，提供 Screen 与鼠标抓取服务。</param>
    /// <param name="widthChanged">拖动期间每个宽度变化触发一次：仅重排布局（对话区让位）。</param>
    /// <param name="dragEnded">拖动释放时触发一次：按最终宽度重渲内容。</param>
    /// <param name="defaultTitle">未提供标题时的兜底标题。</param>
    protected SidebarViewBase(IApplication app, Action widthChanged, Action dragEnded, string defaultTitle)
    {
        _app = app;
        _widthChanged = widthChanged;
        _dragEnded = dragEnded;
        _defaultTitle = defaultTitle;
        _headerTitle = defaultTitle;
        _content = new MessageListView();
        CanFocus = false;
        TabStop = TabBehavior.NoStop;

        var screenWidth = _app.Screen.Width > 0 ? _app.Screen.Width : 120;
        CurrentWidth = Math.Clamp(DefaultWidth, ComputeMinWidth(screenWidth), ComputeMaxWidth(screenWidth));
        Width = CurrentWidth;
        Height = Dim.Fill();
        SetScheme(TuiTheme.ConversationArea);

        // 内容区从标题行下方开始；左侧留 1 列边距与对话区分隔。
        _content.X = 1;
        _content.Y = 1;
        _content.Width = Dim.Fill() - 1;
        _content.Height = Dim.Fill() - 1;
        Add(_content);

        // 启用位置上报，使分隔线悬停状态（PositionReport）可被驱动到本视图。
        MousePositionTracking = true;
        // 鼠标移出侧边栏时清除悬停高亮。
        MouseLeave += (_, _) =>
        {
            if (_isHovering)
            {
                _isHovering = false;
                SetNeedsDraw();
            }
        };
    }

    /// <summary>Replaces the sidebar content with a fresh rendering.</summary>
    public void Update(IReadOnlyList<FormattedLine> lines, string headerTitle)
    {
        _headerTitle = string.IsNullOrWhiteSpace(headerTitle) ? _defaultTitle : headerTitle;
        _content.Clear();
        _content.AppendLines(lines);
        SetNeedsDraw();
    }

    /// <summary>Clears content while keeping the sidebar chrome (header + separator).</summary>
    public void ClearContent()
    {
        _content.Clear();
        SetNeedsDraw();
    }

    /// <summary>
    /// 左侧分隔线是拖动手柄：按住拖动调整宽度。侧边栏右停靠贴屏幕右缘，
    /// 因此新宽度 = 屏幕宽 - 鼠标屏幕 x；clamp 到 [MinWidth, 屏幕宽 60%]。
    /// 拖动期间仅通过 <c>_widthChanged</c> 通知宿主重排布局；释放时通过
    /// <c>_dragEnded</c> 触发一次内容重渲（见类头注释）。
    /// </summary>
    protected override bool OnMouseEvent(Mouse mouse)
    {
        // 拖动中：Grab 后所有鼠标事件都路由到本视图（坐标可能已在面板之外）。
        if (_isDragging)
        {
            if (mouse.Flags.HasFlag(MouseFlags.LeftButtonReleased))
            {
                _isDragging = false;
                _isHovering = false;
                SetNeedsDraw();
                _app.Mouse?.UngrabMouse();
                _dragEnded();
            }
            else
            {
                ApplyDraggedWidth(mouse.ScreenPosition.X);
            }
            return true;
        }

        // 悬停反馈：未拖动时根据鼠标位置（PositionReport）刷新分隔线高亮。
        if (mouse.Flags.HasFlag(MouseFlags.PositionReport))
        {
            var hovering = mouse.Position is { } hoverPos && hoverPos.X < DragHandleWidth;
            if (hovering != _isHovering)
            {
                _isHovering = hovering;
                SetNeedsDraw();
            }
        }

        if (mouse.Flags.HasFlag(MouseFlags.LeftButtonPressed) && mouse.Position is { } pos && pos.X < DragHandleWidth)
        {
            _isDragging = true;
            _isHovering = true;
            SetNeedsDraw();
            _app.Mouse?.GrabMouse(this);
            return true;
        }

        return base.OnMouseEvent(mouse);
    }

    private void ApplyDraggedWidth(int screenX)
    {
        var newWidth = ComputeDraggedWidth(screenX, _app.Screen.Width);
        if (newWidth == CurrentWidth)
            return;

        CurrentWidth = newWidth;
        Width = newWidth;
        X = Pos.AnchorEnd(newWidth);
        _widthChanged();
    }

    /// <summary>键盘调整宽度的步进（列）。Ctrl+Shift+←/→ 每次按键的宽度变化。</summary>
    internal const int KeyboardResizeStep = 4;

    /// <summary>
    /// 键盘调整宽度：按 <paramref name="delta"/> 列增减，clamp 规则与分隔线拖拽一致
    /// （下限 <see cref="MinWidth"/>，上限 <see cref="ComputeMaxWidth(int)"/>：Standard 取「30% 与主区保底 65 列」较小值，Wide 封顶 45 列）。
    /// 宽度实际变化时更新几何并触发 <c>_widthChanged</c>（仅重排布局）。
    /// </summary>
    /// <returns>宽度是否实际变化（被 clamp 抵消时为 false，调用方无需重渲内容）。</returns>
    public bool AdjustWidth(int delta)
    {
        var screenWidth = _app.Screen.Width;
        var newWidth = Math.Clamp(
            CurrentWidth + delta, ComputeMinWidth(screenWidth), ComputeMaxWidth(screenWidth));
        if (newWidth == CurrentWidth)
            return false;

        CurrentWidth = newWidth;
        Width = newWidth;
        X = Pos.AnchorEnd(newWidth);
        _widthChanged();
        return true;
    }

    /// <summary>拖动位置到面板宽度的 clamp 规则：下限 <see cref="MinWidth"/>；上限见
    /// <see cref="ComputeMaxWidth(int)"/>。极窄终端上退化为
    /// 不超过屏幕宽——保证 max ≥ min 且面板永远不会被定位到屏幕外。</summary>
    internal static int ComputeDraggedWidth(int screenX, int screenWidth) =>
        Math.Clamp(screenWidth - screenX, ComputeMinWidth(screenWidth), ComputeMaxWidth(screenWidth));

    /// <summary>
    /// 宽度上限：依据三档断点规范（Compact &lt; 100, Standard 100..140, Wide &gt; 140），
    /// Standard 下上限为屏幕 30%（28..42 列），Wide 下上限为 45 列，
    /// 主会话区保底 <see cref="ChatColumnMinWidth"/>（65 列）。与拖拽及键盘调整共享规则。
    /// </summary>
    internal static int ComputeMaxWidth(int screenWidth)
    {
        if (screenWidth <= MinWidth)
            return screenWidth;

        int targetMax;
        if (screenWidth > TuiSpacing.BreakpointWide)
        {
            // Wide (> 140): 45 columns max (main chat >= 95)
            targetMax = Math.Min(45, screenWidth - ChatColumnMinWidth);
        }
        else if (screenWidth >= TuiSpacing.BreakpointCompact)
        {
            // Standard (100..140): 30% of screen (28..42 columns), main chat >= 65
            targetMax = Math.Min(screenWidth * 3 / 10, screenWidth - ChatColumnMinWidth);
        }
        else
        {
            // Compact (<100): fallback clamped to MinWidth
            targetMax = MinWidth;
        }

        return Math.Min(screenWidth, Math.Max(MinWidth, targetMax));
    }

    /// <summary>宽度下限：极窄终端（&lt; MinWidth）退化为屏幕宽。</summary>
    internal static int ComputeMinWidth(int screenWidth) => Math.Min(MinWidth, screenWidth);

    protected override bool OnDrawingContent(DrawContext? context)
    {
        var w = Viewport.Width;
        if (w <= 0) return false;

        // 标题行：反色条，前景色由具体面板指定（Plan/Team 各用模式主题色）。
        Move(0, 0);
        SetAttribute(HeaderAttribute);
        var title = $" {HeaderGlyph} {_headerTitle}";
        // 拖动时在标题行尾部实时展示当前宽度，作为调整过程的反馈。
        if (_isDragging)
            title += $"  ◂ {CurrentWidth}";
        AddStr(title.Length >= w ? title[..Math.Max(0, w - 1)] : title + new string(' ', w - title.Length));

        // 左侧竖分隔线（拖动手柄）：悬停/拖动时高亮并换用双线字形，提示可横向调整宽度。
        var handleActive = _isHovering || _isDragging;
        SetAttribute(new Attribute(handleActive ? TuiPalette.Accent : TuiPalette.FgMuted, TuiPalette.BgPrimary));
        for (var y = 1; y < Viewport.Height; y++)
        {
            Move(0, y);
            AddRune(handleActive ? '║' : '│');
        }

        return base.OnDrawingContent(context);
    }
}
