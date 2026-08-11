namespace OneCode.App.Tui;

// 纯数据模型，从 MessageListView 提取。
// LineEntry 为内部存储结构；FormattedLine/ToolLineTag/ToolDetailLineTag/
// CodeBlockCopyTag/LineSegment 为跨文件引用的公共类型。

/// <summary>
/// 单行渲染条目：文本 + 颜色 + 可选分段 + 可选背景 + 可选元数据标签。
/// </summary>
internal readonly record struct LineEntry(
    string Text,
    Color Color,
    IReadOnlyList<LineSegment>? Segments,
    Color? Bg = null,
    object? Tag = null);

public sealed class FormattedLine
{
    public string FullText { get; }
    public Color Color { get; }
    public Color? Bg { get; }
    public IReadOnlyList<LineSegment>? Segments { get; }

    /// <summary>Optional metadata tag — used to attach tool-call data for expand/collapse interaction.</summary>
    public object? Tag { get; init; }

    public FormattedLine(string fullText, Color color)
    {
        FullText = fullText;
        Color = color;
    }

    private FormattedLine(string fullText, Color color, IReadOnlyList<LineSegment> segments)
    {
        FullText = fullText;
        Color = color;
        Segments = segments;
    }

    private FormattedLine(string fullText, Color color, Color bg)
    {
        FullText = fullText;
        Color = color;
        Bg = bg;
    }

    public static FormattedLine Plain(string text, Color color) => new(text, color);

    /// <summary>Full-width line with a distinct background color (for mode banners, plan headers).</summary>
    public static FormattedLine WithBackground(string text, Color fg, Color bg) => new(text, fg, bg);

    /// <summary>
    /// 从多段颜色片段创建 FormattedLine。FullText 为所有段文本拼接，
    /// Color 为第一段的颜色（用于兼容单色渲染）。
    /// </summary>
    public static FormattedLine FromSegments(LineSegment[] segments)
    {
        if (segments is null || segments.Length == 0)
            return Plain("", TuiPalette.FgPrimary);
        var text = string.Concat(segments.Select(s => s.Text));
        return new FormattedLine(text, segments[0].Color, segments);
    }

    /// <summary>Creates a segmented line with an attached metadata tag (e.g. <see cref="ToolLineTag"/>).</summary>
    public static FormattedLine FromSegmentsWithTag(LineSegment[] segments, object tag)
    {
        var line = FromSegments(segments);
        return new FormattedLine(line.FullText, line.Color, segments) { Tag = tag };
    }
}

/// <summary>Metadata attached to a tool-call summary line for click-to-expand/collapse.</summary>
public sealed record ToolLineTag(string Name, string? Args, string? Result, bool IsExpanded);

/// <summary>Marker tag for detail lines inserted below an expanded tool line.</summary>
public sealed record ToolDetailLineTag;

/// <summary>Metadata attached to a thinking summary line for click-to-expand/collapse.</summary>
public sealed record ThinkingLineTag(string Content, bool IsExpanded);

/// <summary>Marker tag for lines inserted below an expanded thinking summary.</summary>
public sealed record ThinkingDetailLineTag;

/// <summary>
/// Metadata attached to a fenced code block's header line. Clicking the
/// tagged line copies <see cref="Code"/> to the clipboard.
/// </summary>
public sealed record CodeBlockCopyTag(string Code);

/// <summary>错误行标记——可折叠的错误详情。Content 为完整错误文本。</summary>
public sealed record ErrorLineTag(string Content, bool IsExpanded);

/// <summary>一行中的颜色段——文本 + 前景色 + 可选背景色。</summary>
public sealed record LineSegment(string Text, Color Color, Color? Bg = null);
