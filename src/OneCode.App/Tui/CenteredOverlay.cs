namespace OneCode.App.Tui;

/// <summary>
/// Convenience base class for centred overlay panels — design-spec §3.
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

    /// <summary>Sizing strategy used by <see cref="OverlayHost.Position"/>.</summary>
    public virtual OverlayLayoutMode LayoutMode => OverlayLayoutMode.Dialog;

    /// <summary>The control that receives focus whenever this overlay becomes topmost.</summary>
    protected virtual View? InitialFocusView => null;

    internal void FocusInitialView()
    {
        if (InitialFocusView?.SetFocus() != true)
            SetFocus();
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
        HeaderLabel.SetScheme(TuiTheme.MakeScheme(TuiPalette.Accent, TuiPalette.BgCard));
        Add(HeaderLabel);
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
