namespace OneCode.App.Tui;

// 渲染辅助逻辑——从 MessageListView 提取。
// 纯函数，无实例状态依赖。

internal static class MessageRenderer
{
    /// <summary>
    /// 在展开/折叠时替换三角形符号。支持两种变体：
    /// 小三角（▸ ▾ U+25B8/U+25BE，工具行）和大三角（▶ ▼ U+25B6/U+25BC，思考块）。
    /// </summary>
    public static IReadOnlyList<LineSegment>? ReplaceTriangleSymbol(
        IReadOnlyList<LineSegment>? segments, bool collapsed)
    {
        if (segments is null) return null;
        var result = new LineSegment[segments.Count];
        for (var i = 0; i < segments.Count; i++)
        {
            var seg = segments[i];
            if (seg.Text.Contains('\u25b8') || seg.Text.Contains('\u25be')
                || seg.Text.Contains('\u25b6') || seg.Text.Contains('\u25bc')
                || seg.Text.Contains('\u25cf'))
            {
                var newText = collapsed
                    ? seg.Text
                        .Replace("\u25be", "\u25b8")
                        .Replace("\u25bc", "\u25b6")
                        .Replace("\u25cf", "\u25b8")
                    : seg.Text
                        .Replace("\u25b8", "\u25be")
                        .Replace("\u25b6", "\u25bc")
                        .Replace("\u25cf", "\u25be");
                result[i] = new LineSegment(newText, seg.Color, seg.Bg);
            }
            else
            {
                result[i] = seg;
            }
        }
        return result;
    }

    /// <summary>
    /// 构建工具调用展开后显示的完整详情行（Args + Result，按视口宽度自动换行）。
    /// 折叠行负责保持会话紧凑；用户主动展开后不再二次截断内容。
    /// </summary>
    public static List<LineEntry> BuildToolDetailLines(ToolLineTag tag, int viewportWidth)
    {
        const string detailIndent = "      ";
        var lines = new List<LineEntry>();
        var marker = new ToolDetailLineTag();
        var maxContentWidth = Math.Max(20, viewportWidth - detailIndent.Length);

        if (!string.IsNullOrWhiteSpace(tag.Args))
        {
            var prefixLen = detailIndent.Length + "Args: ".Length;
            var argsMaxWidth = Math.Max(20, viewportWidth - prefixLen);
            var displayArgs = OneCode.Core.Tools.DisplayJsonSerializer.NormalizeForDisplay(tag.Args, writeIndented: false);
            var wrappedArgs = TextWidthHelper.WordWrapByWidth(displayArgs, argsMaxWidth);
            for (var i = 0; i < wrappedArgs.Count; i++)
            {
                if (i == 0)
                {
                    lines.Add(new LineEntry(
                        $"{detailIndent}Args: {wrappedArgs[i]}",
                        TuiPalette.ToolDetailColor,
                        new[] { new LineSegment($"{detailIndent}Args: ", TuiPalette.FgMuted), new LineSegment(wrappedArgs[i], TuiPalette.ToolDetailColor) },
                        Tag: marker));
                }
                else
                {
                    var continuationIndent = detailIndent + "      ";
                    lines.Add(new LineEntry(
                        $"{continuationIndent}{wrappedArgs[i]}",
                        TuiPalette.ToolDetailColor,
                        new[] { new LineSegment($"{continuationIndent}", TuiPalette.FgMuted), new LineSegment(wrappedArgs[i], TuiPalette.ToolDetailColor) },
                        Tag: marker));
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(tag.Result))
        {
            var resultText = FormatResultContent(tag.Result);
            var resultLines = resultText.Replace("\r\n", "\n").Split('\n');
            foreach (var resultLine in resultLines)
            {
                foreach (var wrappedLine in TextWidthHelper.WordWrapByWidth(resultLine, maxContentWidth))
                {
                    lines.Add(new LineEntry(
                        $"{detailIndent}{wrappedLine}",
                        TuiPalette.FgSecondary,
                        null,
                        Tag: marker));
                }
            }
        }

        if (lines.Count == 0)
        {
            lines.Add(new LineEntry(
                $"{detailIndent}(no details available)",
                TuiPalette.FgMuted,
                null,
                Tag: marker));
        }

        return lines;
    }

    /// <summary>
    /// Normalizes JSON, JSON-string and mixed-text result content for readability in
    /// the expanded tool detail view without altering non-Unicode backslash escapes.
    /// </summary>
    private static string FormatResultContent(string result)
        => OneCode.Core.Tools.DisplayJsonSerializer.NormalizeForDisplay(result);

    /// <summary>按显示宽度截断文本（处理 CJK 双宽字符）。</summary>
    public static string TruncateVisual(string text, int maxWidth)
    {
        if (maxWidth <= 0) return "";
        return TextWidthHelper.TruncateByWidth(text, maxWidth);
    }
}
