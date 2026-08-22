namespace OneCode.App.Tui;

public enum LineRole
{
    Assistant,
    System,
    Error,
    DiffAdded,
    DiffRemoved,
    DiffHunk,
}

public sealed class ConvLine
{
    public LineRole Role { get; }
    public string Text { get; }

    /// <summary>
    /// Optional multi-color segments. When set, the renderer uses these
    /// instead of the single-color <see cref="Text"/> to produce a
    /// multi-colored <see cref="FormattedLine"/>.
    /// </summary>
    public IReadOnlyList<LineSegment>? Segments { get; }

    /// <summary>
    /// Optional metadata tag attached to this line for click interaction
    /// (e.g. tool-line expand/collapse).
    /// </summary>
    public object? Tag { get; }

    /// <summary>
    /// Optional background color for the entire line. When set, the renderer
    /// fills the line background with this color before drawing the text.
    /// </summary>
    public Color? Bg { get; }

    public ConvLine(LineRole role, string text)
    {
        Role = role;
        Text = text;
    }

    public ConvLine(LineRole role, string text, IReadOnlyList<LineSegment>? segments, object? tag = null, Color? bg = null)
    {
        Role = role;
        Text = text;
        Segments = segments;
        Tag = tag;
        Bg = bg;
    }
}
