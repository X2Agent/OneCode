namespace OneCode.App.Tui;

/// <summary>
/// Braille-dot spinner shown during assistant "thinking", similar to the TS Ink spinner.
/// Animation logic is delegated to <see cref="SpinnerController"/>.
/// </summary>
public sealed class SpinnerView : View
{
    private readonly SpinnerController _spinner;
    private string _label = string.Empty;

    public SpinnerView(IApplication app)
    {
        _spinner = new SpinnerController(app, SetNeedsDraw);
        Width = Dim.Fill();
        Height = 1;
        CanFocus = false;
    }

    /// <summary>Text shown next to the spinner (default <c>Thinking?</c>).</summary>
    public string Label
    {
        get => _label;
        set
        {
            _label = value;
            SetNeedsDraw();
        }
    }

    /// <summary>Whether the animation timeout is active.</summary>
    public bool IsRunning => _spinner.IsRunning;

    /// <summary>
    /// Starts the spinner and schedules frame updates on the main loop (Terminal.Gui timeout).
    /// </summary>
    public void Start(string label = "处理中\u2026")
    {
        _label = label;
        Visible = true;
        _spinner.Start();
        SetNeedsDraw();
    }

    /// <summary>
    /// Stops the spinner animation and removes the scheduled timeout.
    /// </summary>
    public void Stop()
    {
        _spinner.Stop();
        Visible = false;
        SetNeedsDraw();
    }

    protected override bool OnDrawingContent(DrawContext? context)
    {
        base.OnDrawingContent(context);

        if (!Visible) return true;

        var viewport = Viewport;
        var frame = _spinner.CurrentFrame;
        var text = string.IsNullOrEmpty(_label)
            ? frame
            : $"{frame} {_label}";

        Move(0, 0);
        SetAttribute(TuiTheme.SpinnerColor);
        AddStr(text.Length > viewport.Width ? text[..viewport.Width] : text);
        return true;
    }
}
