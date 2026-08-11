namespace OneCode.App.Tui;

// 渲染辅助逻辑——从 ChatTranscriptView 提取。
// 纯函数 + 共享常量，无实例状态依赖。

internal static class ConversationRenderer
{
    public static readonly int ContentIndent = TuiSpacing.MessageContentIndent;
    public static readonly string Indent = new(' ', ContentIndent);

    /// <summary>
    /// 构建已完成工具调用的格式化行（工具名 + 目标 + 结果摘要 + 耗时）。
    /// </summary>
    public static FormattedLine MakeCompletedToolLine(
        string name, bool isError, string? toolInput, string? duration, string? result = null)
    {
        var statusColor = isError ? TuiPalette.Error : TuiPalette.Success;
        var segments = new List<LineSegment>
        {
            new($"{Indent}", TuiPalette.BgPrimary),
            new($"{TuiGlyphs.ToolCall} ", TuiPalette.Accent),
            new(name, TuiPalette.Warning),
        };

        // 使用 ToolResultSummarizer 格式化目标（文件路径、命令等）
        var target = ToolResultSummarizer.FormatTarget(name, toolInput);
        if (!string.IsNullOrWhiteSpace(target))
            segments.Add(new($" {target}", TuiPalette.ToolDetailColor));

        if (!isError && !string.IsNullOrEmpty(result))
        {
            var summary = ToolResultSummarizer.Summarize(name, result, toolInput);
            if (!string.IsNullOrEmpty(summary))
                segments.Add(new($" \u00b7 {summary}", statusColor));
        }

        if (!string.IsNullOrEmpty(duration))
            segments.Add(new($" \u00b7 {duration}", TuiPalette.FgMuted));
        else if (isError)
            segments.Add(new(" \u00b7 error", statusColor));

        var tag = new ToolLineTag(name, toolInput, result, IsExpanded: false);
        return FormattedLine.FromSegmentsWithTag(segments.ToArray(), tag);
    }

    /// <summary>构建流式通知行（横线分隔 + 文本）。</summary>
    public static FormattedLine MakeStreamingNotice(string text, Color color)
        => FormattedLine.FromSegments(new[]
        {
            new LineSegment($"{Indent}", TuiPalette.BgPrimary),
            new LineSegment($"{TuiGlyphs.BorderHorizontal} ", TuiPalette.FgMuted),
            new LineSegment(text, color),
        });

    /// <summary>按显示宽度换行文本（处理 CJK 双宽字符）。</summary>
    public static List<string> WordWrapStreaming(string text, int maxWidth)
        => TextWidthHelper.WordWrapByWidth(text, maxWidth);
}
