namespace OneCode.App.Tui;

/// <summary>
/// Centralised overlay (popup) manager for the TUI — design-spec §3.
///
/// Owns the lifecycle of modal panels (/diff Review, Ctrl+Shift+D LSP,
/// Settings, Resume chooser, Diff detail, etc.).
/// The host exposes:
/// <list type="bullet">
///   <item><see cref="Push"/> / <see cref="Pop"/> — stack-based modal layering</item>
///   <item><see cref="IsOverlayVisible"/> — whether any overlay is on-screen</item>
/// </list>
///
/// Overlays are simple <see cref="View"/> subclasses; the host takes care
/// of adding/removing them from the parent <see cref="ReplShell"/>, sizing
/// them to the middle of the screen, and forwarding keyboard input.
/// Overlay base classes (<see cref="CenteredOverlay"/>,
/// <see cref="ResultOverlay{TResult}"/>) live in their own files.
/// </summary>
public sealed class OverlayHost : View
{
    private readonly Action _focusBackground;
    private readonly Stack<View> _stack = new();
    private int _lastHostWidth = -1;
    private int _lastHostHeight = -1;

    public OverlayHost(ReplShell shell)
        : this(CreateBackgroundFocusAction(shell))
    {
    }

    private static Action CreateBackgroundFocusAction(ReplShell shell)
    {
        ArgumentNullException.ThrowIfNull(shell);
        return shell.FocusChatInput;
    }

    internal OverlayHost(Action focusBackground)
    {
        ArgumentNullException.ThrowIfNull(focusBackground);
        _focusBackground = focusBackground;
        CanFocus = true;
        TabStop = TabBehavior.TabGroup;
        Width = Dim.Fill();
        Height = Dim.Fill();
    }

    /// <summary>True when at least one overlay is currently shown.</summary>
    public bool IsOverlayVisible => _stack.Count > 0;

    /// <summary>Number of currently stacked overlays (for tests / diagnostics).</summary>
    public int Depth => _stack.Count;

    internal View? Top => _stack.TryPeek(out var overlay) ? overlay : null;

    /// <summary>Show an overlay on top of the stack.</summary>
    public void Push(View overlay)
    {
        ArgumentNullException.ThrowIfNull(overlay);
        foreach (var v in _stack)
            if (v is CenteredOverlay co) co.IsTopMost = false;

        _stack.Push(overlay);
        Add(overlay);
        Position(overlay);
        FocusOverlay(overlay);
        SetNeedsDraw();
    }

    /// <summary>
    /// Close the topmost overlay programmatically. Result-bearing overlays are notified
    /// before removal so their awaiting task always completes.
    /// </summary>
    public bool Pop() => CloseTop(OverlayCloseReason.Programmatic);

    /// <summary>Close the topmost overlay for the supplied reason.</summary>
    public bool CloseTop(OverlayCloseReason reason)
    {
        if (_stack.Count == 0)
            return false;

        var overlay = _stack.Pop();
        if (overlay is IOverlayDismissible dismissible)
            dismissible.Dismiss(reason);
        Remove(overlay);

        if (_stack.Count > 0)
        {
            if (_stack.Peek() is CenteredOverlay newTop)
            {
                newTop.IsTopMost = true;
                newTop.SetNeedsDraw();
            }

            FocusOverlay(_stack.Peek());
        }
        else
        {
            Visible = false;
            _focusBackground();
        }

        SetNeedsDraw();
        return true;
    }

    /// <summary>Close every overlay (e.g. on quit) and complete pending results.</summary>
    public void PopAll()
    {
        while (_stack.Count > 0)
            CloseTop(OverlayCloseReason.HostShutdown);
    }

    /// <summary>Dispatch an Esc keypress to the topmost overlay.</summary>
    public bool HandleEsc() => CloseTop(OverlayCloseReason.Escape);

    private static void FocusOverlay(View overlay)
    {
        if (overlay is CenteredOverlay centered)
            centered.FocusInitialView();
        else
            overlay.SetFocus();
    }

    /// <summary>
    /// Re-layout every stacked overlay against the current host size.
    /// Call after terminal resize and after the host first becomes visible
    /// (Viewport is often 0×0 while the host was hidden).
    /// </summary>
    public void RepositionAll()
    {
        if (_stack.Count == 0) return;
        foreach (var overlay in _stack)
            Position(overlay);
        SetNeedsDraw();
    }

    /// <summary>Position an overlay centred (or filled) within the host.</summary>
    public void Position(View overlay)
    {
        var (sw, sh) = ResolveHostSize();
        if (sw <= 0 || sh <= 0)
            return; // First draw / RepositionAll will retry once Viewport is valid.

        int w;
        int h;
        if (overlay is CenteredOverlay { LayoutMode: OverlayLayoutMode.Fill })
        {
            // Near-fullscreen: 1-cell margin on each side.
            w = Math.Max(20, sw - 2);
            h = Math.Max(8, sh - 2);
        }
        else
        {
            var (prefW, prefH) = overlay is CenteredOverlay co
                ? co.GetPreferredSize()
                : (TuiSpacing.OverlayDefaultWidth, TuiSpacing.OverlayDefaultHeight);

            // Dialogs grow with the terminal but stay clearly inset (not full-bleed).
            var maxW = Math.Max(40, sw * 70 / 100);
            var maxH = Math.Max(12, sh * 75 / 100);
            w = Math.Clamp(prefW, 40, maxW);
            h = Math.Clamp(prefH, 10, maxH);
        }

        overlay.X = Math.Max(0, (sw - w) / 2);
        overlay.Y = Math.Max(0, (sh - h) / 2);
        overlay.Width = w;
        overlay.Height = h;
    }

    /// <summary>
    /// Prefer own Viewport; when still 0×0 (host was hidden), fall back to the
    /// parent <see cref="ReplShell"/> frame so the first Push is not a 20×8 stub.
    /// </summary>
    private (int Width, int Height) ResolveHostSize()
    {
        var w = Viewport.Width;
        var h = Viewport.Height;
        if (w > 0 && h > 0)
            return (w, h);

        if (SuperView is { } parent)
        {
            w = parent.Frame.Width;
            h = parent.Frame.Height;
            if (w > 0 && h > 0)
                return (w, h);
            w = parent.Viewport.Width;
            h = parent.Viewport.Height;
        }

        return (w, h);
    }

    protected override bool OnDrawingContent(DrawContext? context)
    {
        // Host size is often wrong on the Push that follows Visible=true, and
        // always wrong after a maximize/restore until we re-clamp overlays here.
        var w = Viewport.Width;
        var h = Viewport.Height;
        if (_stack.Count > 0 && w > 0 && h > 0
            && (w != _lastHostWidth || h != _lastHostHeight))
        {
            _lastHostWidth = w;
            _lastHostHeight = h;
            RepositionAll();
        }

        return base.OnDrawingContent(context);
    }

    protected override bool OnKeyDown(Key kb)
    {
        if (kb == Key.Esc && _stack.Count > 0)
            return HandleEsc();

        return base.OnKeyDown(kb);
    }
}
