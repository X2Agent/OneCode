using System.Buffers;
using System.Text;

namespace OneCode.Tui;

/// <summary>
/// 终端显示宽度测量、换行与裁剪。
///
/// 宽度口径唯一来源是 Terminal.Gui 的 <see cref="Terminal.Gui.Text.RuneExtensions.GetColumns"/>（wcwidth 移植）：
/// 自建区间表会与终端/框架的判定分叉（例如符号区段实际占 1 列，按 2 列测量会让所有补白算错、
/// 边框与竖线错位）。绘制层由 Terminal.Gui 排版，测量层必须用同一套规则。
/// </summary>
public static class TextWidthHelper
{
    /// <summary>
    /// 单个 rune 的终端列宽。不可打印字符（GetColumns 返回 -1）按 0 列计，
    /// 与绘制层“画不出来也不占位”的语义一致。
    /// </summary>
    private static int RuneColumns(Rune rune)
    {
        var columns = Terminal.Gui.Text.RuneExtensions.GetColumns(rune);
        return columns < 0 ? 0 : columns;
    }

    /// <summary>字符串占用的终端列数。</summary>
    public static int GetDisplayWidth(string text)
    {
        if (string.IsNullOrEmpty(text))
            return 0;

        var width = 0;
        var span = text.AsSpan();
        while (!span.IsEmpty)
        {
            // 孤立代理项不是合法标量值：绘制前会被剔除，测量时同样记 0 列以保持两侧一致。
            if (Rune.DecodeFromUtf16(span, out var rune, out var consumed) == OperationStatus.Done)
                width += RuneColumns(rune);
            span = span[consumed..];
        }

        return width;
    }

    /// <summary>
    /// 移除孤立代理项（不成对的 high/low surrogate），合法代理对原样保留。
    /// 孤立代理项不是合法 Unicode 标量值，传入 View.AddStr 会令 Terminal.Gui 抛
    /// <see cref="ArgumentException"/> 整屏崩溃。
    /// </summary>
    private static string StripInvalidScalars(string text)
    {
        if (string.IsNullOrEmpty(text))
            return text;

        var span = text.AsSpan();
        var cursor = 0;
        while (!span.IsEmpty)
        {
            var status = Rune.DecodeFromUtf16(span, out _, out var consumed);
            if (status == OperationStatus.Done)
            {
                cursor += consumed;
                span = span[consumed..];
                continue;
            }

            // 干净文本不进此分支，零分配；重建时合法代理对整体保留。
            var builder = new StringBuilder(text.Length);
            builder.Append(text, 0, cursor);
            while (!span.IsEmpty)
            {
                var inner = Rune.DecodeFromUtf16(span, out _, out var innerConsumed);
                if (inner == OperationStatus.Done)
                    builder.Append(span[..innerConsumed]);
                span = span[innerConsumed..];
            }

            return builder.ToString();
        }

        return text;
    }

    /// <summary>
    /// 把文本压成单行：真实换行/制表符替换为空格并折叠连续空白。
    /// 折叠态工具行按单行布局绘制，宽度测量把 <c>\n</c> 计为 1 列，
    /// 但绘制期会按换行处理，含换行的参数会令整行错位——故折叠路径必须先单行化。
    /// 展开详情路径不受影响（那里需要真实换行）。
    /// </summary>
    public static string CollapseToSingleLine(string text)
    {
        if (string.IsNullOrEmpty(text))
            return text;
        if (text.IndexOfAny(['\n', '\r', '\t']) < 0)
            return text;

        var builder = new StringBuilder(text.Length);
        var lastWasSpace = false;
        foreach (var ch in text)
        {
            var isBreak = ch is '\n' or '\r' or '\t';
            if (isBreak || ch == ' ')
            {
                if (lastWasSpace)
                    continue;
                builder.Append(' ');
                lastWasSpace = true;
                continue;
            }
            builder.Append(ch);
            lastWasSpace = false;
        }
        return builder.ToString().Trim();
    }

    /// <summary>
    /// 按显示宽度截断，超出时追加省略号（占用 1 列，因此可用宽度预算为 <c>maxDisplayWidth - 1</c>）。
    /// <paramref name="maxDisplayWidth"/> 为 0 时返回空串。
    /// </summary>
    public static string TruncateByWidth(string text, int maxDisplayWidth)
    {
        if (maxDisplayWidth <= 0) return "";
        if (string.IsNullOrEmpty(text)) return text;

        text = StripInvalidScalars(text);
        var budget = maxDisplayWidth - 1;
        var width = 0;
        var index = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            var columns = RuneColumns(rune);
            if (width + columns > budget)
                return text[..index] + "\u2026";
            width += columns;
            index += rune.Utf16SequenceLength;
        }

        return text;
    }

    /// <summary>
    /// 按显示宽度硬裁剪，不追加省略号。跨边界的宽字符整字丢弃。
    /// 这是绘制期的视口硬边界：恰好满宽的行必须保留每个字符，为省略号预留列会把
    /// 行尾真实字符替换成 "…"，让换行后的工具 JSON 行看起来"右侧显示不全"。
    /// </summary>
    public static string ClipByWidth(string text, int maxDisplayWidth)
    {
        if (maxDisplayWidth <= 0) return "";
        if (string.IsNullOrEmpty(text)) return text;

        // 孤立代理项会使 AddStr 崩溃，先剔除；宽度按剔除后文本计算保持一致。
        text = StripInvalidScalars(text);

        var width = 0;
        var index = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            var columns = RuneColumns(rune);
            if (width + columns > maxDisplayWidth)
                return text[..index];
            width += columns;
            index += rune.Utf16SequenceLength;
        }

        return text;
    }

    /// <summary>
    /// 按显示宽度换行：保留显式换行，优先在空格处断行，CJK 等无空格文本任意字符处可断，
    /// 宽字符按实际列宽累加。
    /// </summary>
    public static List<string> WordWrapByWidth(string text, int maxWidth)
    {
        List<string> result = [];
        if (string.IsNullOrEmpty(text)) return result;
        if (maxWidth <= 0) maxWidth = 40;

        // 先剔除孤立代理项，与 ClipByWidth 一致：换行产物必须可直接渲染。
        text = StripInvalidScalars(text);

        foreach (var paragraph in text.Replace("\r\n", "\n").Split('\n'))
        {
            if (paragraph.Length == 0)
                result.Add("");
            else
                WrapParagraph(paragraph, maxWidth, result);
        }

        return result;
    }

    private static void WrapParagraph(string paragraph, int maxWidth, List<string> result)
    {
        var lineStart = 0;      // 当前行在 paragraph 中的起始下标
        var lineWidth = 0;      // 当前行已占列数
        var lastSpace = -1;     // 最近一个可断行的空格下标
        var lastSpaceWidth = 0; // 到该空格之前（不含空格）的列数

        var i = 0;
        while (i < paragraph.Length)
        {
            var rune = Rune.GetRuneAt(paragraph, i);
            var length = rune.Utf16SequenceLength;
            var columns = RuneColumns(rune);

            if (rune.Value == ' ')
            {
                lastSpace = i;
                lastSpaceWidth = lineWidth;

                if (lineWidth + columns > maxWidth)
                {
                    result.Add(paragraph[lineStart..i].TrimEnd());
                    i += length;
                    lineStart = i;
                    lineWidth = 0;
                    lastSpace = -1;
                    continue;
                }

                lineWidth += columns;
                i += length;
                continue;
            }

            if (lineWidth + columns > maxWidth)
            {
                if (lastSpace >= 0)
                {
                    // 优先在空格处断行：空格本身丢弃，行尾空白裁掉。
                    result.Add(paragraph[lineStart..lastSpace].TrimEnd());
                    lineStart = lastSpace + 1;
                    lineWidth -= lastSpaceWidth + 1;
                    lastSpace = -1;

                    if (lineWidth + columns > maxWidth && i > lineStart)
                    {
                        result.Add(paragraph[lineStart..i].TrimEnd());
                        lineStart = i;
                        lineWidth = 0;
                    }
                }
                else
                {
                    // 无空格可断（CJK）：在当前位置硬断。
                    if (i > lineStart)
                    {
                        result.Add(paragraph[lineStart..i].TrimEnd());
                        lineStart = i;
                        lineWidth = 0;
                    }
                }
            }

            lineWidth += columns;
            i += length;
        }

        if (i > lineStart)
            result.Add(paragraph[lineStart..i].TrimEnd());
    }
}
