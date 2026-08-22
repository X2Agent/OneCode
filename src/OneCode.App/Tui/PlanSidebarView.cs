namespace OneCode.App.Tui;

/// <summary>
/// Right-docked plan sidebar — replaces the in-transcript plan card.
///
/// The full plan (title, document path, markdown, step list, phase state)
/// renders here instead of the conversation flow, keeping the chat column clean.
/// Content scrolling reuses <see cref="MessageListView"/> (mouse wheel + keyboard
/// when focused); the header line is fixed and never scrolls away.
///
/// Width: defaults to <see cref="DefaultWidth"/>; the left separator line is a
/// drag handle — press and drag it to resize (mouse grab keeps tracking while the
/// pointer leaves the panel). Clamped to [MinWidth, 60% of screen]. During the
/// drag only layout is refreshed (panel width + host reflow); on release the
/// plan content and the conversation list re-render once at the final width —
/// full markdown re-wrap per mouse-move made dragging janky and reset scroll
/// positions.
///
/// Visibility is driven by <see cref="ReplShell"/>: auto-shown when a plan exists
/// (first plan submission), auto-hidden when the plan is cleared. Ctrl+G toggles
/// manually (KeybindingDefaults.ActionChatTogglePlanPanel).
/// </summary>
internal sealed class PlanSidebarView : View
{
    public const int DefaultWidth = 50;
    public const int MinWidth = 32;

    /// <summary>分隔线拖动手柄的宽度（列）。未处理鼠标事件会冒泡到本视图，
    /// 因此手柄区可覆盖分隔线列及紧邻的一列内容列，便于抓取。</summary>
    private const int DragHandleWidth = 2;

    /// <summary>拖动调整宽度时给对话区保留的最小列数，防止面板挤占整个屏幕。</summary>
    private const int ChatColumnMinWidth = 20;

    private const string HeaderGlyph = "U0001f4cb";

    private readonly IApplication _app;
    private readonly MessageListView _content;
    private readonly Action _widthChanged;
    private readonly Action _dragEnded;
    private string _headerTitle = "计划";
    private bool _isDragging;
    private bool _isHovering;

    /// <summary>Current panel width in columns (mutable via separator drag).</summary>
    public int CurrentWidth { get; private set; } = DefaultWidth;

    /// <param name="widthChanged">拖动期间每个宽度变化触发一次：仅重排布局（对话区让位）。</param>
    /// <param name="dragEnded">拖动释放时触发一次：按最终宽度重渲计划内容。</param>
    public PlanSidebarView(IApplication app, Action widthChanged, Action dragEnded)
    {
        _app = app;
        _widthChanged = widthChanged;
        _dragEnded = dragEnded;
        _content = new MessageListView();
        CanFocus = false;
        TabStop = TabBehavior.NoStop;
        Width = DefaultWidth;
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

    /// <summary>Replaces the sidebar content with a fresh plan rendering.</summary>
    public void Update(IReadOnlyList<FormattedLine> lines, string headerTitle)
    {
        _headerTitle = string.IsNullOrWhiteSpace(headerTitle) ? "计划" : headerTitle;
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
        // 任意移动事件都刷新宽度、释放结束拖动——比按特定 flag 判断更稳健，
        // 兼容不同终端/驱动对拖动移动事件的不同上报方式（splitter 语义）。
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
        // 内容区未处理的鼠标事件会冒泡到本视图，因此可据此区分"在手柄区"与"在手柄区外"。
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

    /// <summary>
    /// 拖动位置到面板宽度的 clamp 规则：下限 <see cref="MinWidth"/>；上限取
    /// 「屏幕 60%」与「屏幕宽 - 对话区最小宽度」的较小值，避免面板挤占整个屏幕。
    /// 极窄终端上各限值相互冲突（&lt;32 列时 MinWidth 本身就超过屏幕宽），
    /// 此时退化为不超过屏幕宽——保证 max ≥ min（<see cref="Math.Clamp"/> 契约）
    /// 且面板永远不会被定位到屏幕外。
    /// </summary>
    internal static int ComputeDraggedWidth(int screenX, int screenWidth)
    {
        var maxWidth = Math.Min(screenWidth, Math.Max(MinWidth, Math.Min(screenWidth * 3 / 5, screenWidth - ChatColumnMinWidth)));
        var minWidth = Math.Min(MinWidth, screenWidth);
        return Math.Clamp(screenWidth - screenX, minWidth, maxWidth);
    }

    protected override bool OnDrawingContent(DrawContext? context)
    {
        var w = Viewport.Width;
        if (w <= 0) return false;

        // 标题行：反色条与对话流中的 plan card 头部一致（ModePlanFg on BgTerminalHeader）。
        Move(0, 0);
        SetAttribute(new Attribute(TuiPalette.ModePlanFg, TuiPalette.BgTerminalHeader));
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
