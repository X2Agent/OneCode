namespace OneCode.App.Tui;

/// <summary>
/// Convenience base class for centred overlay panels.
/// Provides automatic sizing, box-drawing border, and a title bar with accent color.
/// Lifecycle management lives in <see cref="OverlayHost"/>; result-bearing
/// close semantics in <see cref="ResultOverlay{TResult}"/>.
/// </summary>
public abstract class CenteredOverlay : View
{
    protected Label HeaderLabel { get; }

    public new string Title
    {
        get => HeaderLabel.Text;
        set => HeaderLabel.Text = value;
    }

    /// <summary>
    /// Whether this overlay is the top-most on the stack (focused).
    /// Top-most uses <see cref="TuiPalette.BorderAccent"/> (cyan) border;
    /// others use <see cref="TuiPalette.Border"/> (dark gray).
    /// Updated by <see cref="OverlayHost"/> on Push/Pop.
    /// </summary>
    public bool IsTopMost { get; set; } = true;

    /// <summary>Sizing strategy used by <see cref="OverlayHost.Position(Terminal.Gui.ViewBase.View)"/>.</summary>
    public virtual OverlayLayoutMode LayoutMode => OverlayLayoutMode.Dialog;

    /// <summary>The control that receives focus whenever this overlay becomes topmost.</summary>
    protected virtual View? InitialFocusView => null;

    /// <summary>
    /// 聚焦 <see cref="InitialFocusView"/>；未指定或聚焦被否决时，退化为子树中
    /// 第一个可交互的叶子控件（列表/按钮/文本框等）。焦点必须落在叶子上，
    /// 绝不停留在 overlay 根部或 FrameView 等容器：容器 SetFocus 只保证自身
    /// <c>_hasFocus</c>，RestoreFocus/AdvanceFocus 下沉失败时焦点会滞留在
    /// 边框/标题上（McpConfigOverlay 的红框缺陷），且容器自身没有按键绑定，
    /// ←/→ 等按键会失控冒泡到 shell 层走会话记录导航。
    /// </summary>
    internal virtual void FocusInitialView()
    {
        if (InitialFocusView is { } initial && (initial.SetFocus() || initial.HasFocus))
        {
            return;
        }

        FindFocusableLeaf(this)?.SetFocus();
    }

    /// <summary>
    /// 按子视图顺序深度优先查找第一个可聚焦叶子：优先下钻 CanFocus 容器
    /// （如 FrameView 内的列表），容器仅在其整个子树都不可聚焦时才作为候选。
    /// </summary>
    private static View? FindFocusableLeaf(View view)
    {
        foreach (View subView in view.SubViews)
        {
            if (FindFocusableLeaf(subView) is { } leaf)
            {
                return leaf;
            }

            if (subView.CanFocus)
            {
                return subView;
            }
        }

        return null;
    }

    protected CenteredOverlay(string title, int preferredWidth = TuiSpacing.OverlayDefaultWidth, int preferredHeight = TuiSpacing.OverlayDefaultHeight)
    {
        HeaderLabel = new Label
        {
            X = TuiSpacing.OverlayHeaderX,
            Y = TuiSpacing.OverlayHeaderY,
            Width = Dim.Fill() - (TuiSpacing.OverlayHeaderX * 2),
            Height = 1,
            Text = title,
            CanFocus = false,
        };
        HeaderLabel.SetScheme(TuiStyles.MakeScheme(TuiPalette.Accent, TuiPalette.BgCard));
        Add(HeaderLabel);
        // 同时声明 Preferred*：Position/GetPreferredSize 以它们为准做钳制
        //（Width/Height 仅作为首次定位前的占位尺寸）。
        PreferredWidth = preferredWidth;
        PreferredHeight = preferredHeight;
        Width = preferredWidth;
        Height = preferredHeight;
        CanFocus = true;
        TabStop = TabBehavior.TabGroup;
    }

    /// <summary>Returns the preferred (width, height) for centring.
    /// Adds 2 rows for border top/bottom.</summary>
    public virtual (int Width, int Height) GetPreferredSize() => (PreferredWidth, PreferredHeight + 2);

    public int PreferredWidth { get; set; } = TuiSpacing.OverlayDefaultWidth;
    public int PreferredHeight { get; set; } = TuiSpacing.OverlayDefaultHeight;

    protected override bool OnDrawingContent(DrawContext? context)
    {
        var w = Viewport.Width;
        var h = Viewport.Height;
        if (w <= 0 || h <= 0) return false;

        var borderColor = IsTopMost ? TuiPalette.BorderAccent : TuiPalette.Border;
        var border = new Attribute(borderColor, TuiPalette.BgCard);
        var bg = new Attribute(TuiPalette.FgMuted, TuiPalette.BgCard);

        // Fill background
        for (var row = 0; row < h; row++)
        {
            Move(0, row);
            SetAttribute(bg);
            AddStr(new string(' ', w));
        }

        // Top border: ┌─── title ───┐  (DESIGN.md: 方角单线)
        Move(0, 0);
        SetAttribute(border);
        AddStr(TuiGlyphs.BorderTopLeft);
        AddStr(new string(TuiGlyphs.BorderHorizontal[0], Math.Max(0, w - 2)));
        if (w > 1) { Move(w - 1, 0); AddStr(TuiGlyphs.BorderTopRight); }

        // Bottom border: └───┘
        Move(0, h - 1);
        SetAttribute(border);
        AddStr(TuiGlyphs.BorderBottomLeft);
        AddStr(new string(TuiGlyphs.BorderHorizontal[0], Math.Max(0, w - 2)));
        if (w > 1) { Move(w - 1, h - 1); AddStr(TuiGlyphs.BorderBottomRight); }

        // Side borders
        for (var row = 1; row < h - 1; row++)
        {
            Move(0, row);
            SetAttribute(border);
            AddStr(TuiGlyphs.BorderVertical);
            Move(w - 1, row);
            AddStr(TuiGlyphs.BorderVertical);
        }

        // Header separator: ├───┤
        if (h > 2)
        {
            Move(0, 2);
            SetAttribute(border);
            AddStr(TuiGlyphs.BorderLeftTee);
            AddStr(new string(TuiGlyphs.BorderHorizontal[0], Math.Max(0, w - 2)));
            if (w > 1) { Move(w - 1, 2); AddStr(TuiGlyphs.BorderRightTee); }
        }

        return base.OnDrawingContent(context);
    }
}
