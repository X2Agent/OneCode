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

public sealed class ConvLine(
    LineRole role,
    string text,
    IReadOnlyList<LineSegment>? segments = null,
    object? tag = null,
    Color? bg = null)
{
    public LineRole Role { get; } = role;
    public string Text { get; } = text;

    /// <summary>
    /// Optional multi-color segments. When set, the renderer uses these
    /// instead of the single-color <see cref="Text"/> to produce a
    /// multi-colored <see cref="FormattedLine"/>.
    /// </summary>
    public IReadOnlyList<LineSegment>? Segments { get; } = segments;

    /// <summary>
    /// Optional metadata tag attached to this line for click interaction
    /// (e.g. tool-line expand/collapse).
    /// </summary>
    public object? Tag { get; } = tag;

    /// <summary>
    /// Optional background color for the entire line. When set, the renderer
    /// fills the line background with this color before drawing the text.
    /// </summary>
    public Color? Bg { get; } = bg;
}
