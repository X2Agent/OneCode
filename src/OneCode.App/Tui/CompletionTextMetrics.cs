namespace OneCode.App.Tui;

/// <summary>
/// 补全列表的文本度量：显示宽度估算（CJK 等宽字符为 2）、按显示宽度对齐与换行。
/// 与 <see cref="TextWidthHelper"/> 独立并存——换行语义面向补全弹窗布局
/// （尽量在空格处断行 + TrimEnd），与通用词包裹不同，不强行合并。
/// </summary>
internal static class CompletionTextMetrics
{
    internal static int TryGetConsoleWidth()
    {
        try
        {
            var w = Console.WindowWidth;
            return w > 0 ? w : 0;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>估算字符在终端中的显示宽度（CJK 等宽字符为 2，其余为 1）。</summary>
    private static int CharWidth(char c)
    {
        if (c < 0x20 || c == 0x7F) return 0;
        if (c >= 0x1100 && (
            c <= 0x115F ||
            (c >= 0x2E80 && c <= 0x303E) ||
            (c >= 0x3041 && c <= 0x33FF) ||
            (c >= 0x3400 && c <= 0x4DBF) ||
            (c >= 0x4E00 && c <= 0x9FFF) ||
            (c >= 0xA000 && c <= 0xA4CF) ||
            (c >= 0xAC00 && c <= 0xD7A3) ||
            (c >= 0xF900 && c <= 0xFAFF) ||
            (c >= 0xFE30 && c <= 0xFE4F) ||
            (c >= 0xFF00 && c <= 0xFF60) ||
            (c >= 0xFFE0 && c <= 0xFFE6)))
            return 2;
        return 1;
    }

    internal static int DisplayWidth(string s)
    {
        var width = 0;
        foreach (var c in s)
            width += CharWidth(c);
        return width;
    }

    /// <summary>按显示宽度右填充空格，使后续文本对齐。</summary>
    internal static string PadRightDisplay(string s, int targetWidth)
    {
        var current = DisplayWidth(s);
        return current >= targetWidth ? s : s + new string(' ', targetWidth - current);
    }

    /// <summary>
    /// 按显示宽度对文本进行自动换行，尽量在空格处断行；
    /// 无空格时（如中文）按字符断行。
    /// </summary>
    internal static List<string> WordWrap(string text, int maxWidth)
    {
        if (maxWidth <= 0)
            return string.IsNullOrEmpty(text) ? [""] : [text];
        if (string.IsNullOrEmpty(text))
            return [""];

        var lines = new List<string>();
        var pos = 0;

        while (pos < text.Length)
        {
            var remaining = text[pos..];
            if (DisplayWidth(remaining) <= maxWidth)
            {
                lines.Add(remaining);
                break;
            }

            // 找到不超过 maxWidth 的最大子串末尾。
            var end = pos;
            var width = 0;
            while (end < text.Length && width + CharWidth(text[end]) <= maxWidth)
            {
                width += CharWidth(text[end]);
                end++;
            }

            // 尝试在最近的空格处断行（避免单词被截断）。
            if (end < text.Length && end > pos)
            {
                var lastSpace = text.LastIndexOf(' ', end - 1, end - pos);
                if (lastSpace > pos)
                    end = lastSpace + 1;
            }

            lines.Add(text[pos..end].TrimEnd());
            pos = end;

            // 跳过前导空格
            while (pos < text.Length && text[pos] == ' ')
                pos++;
        }

        return lines.Count > 0 ? lines : [""];
    }
}
