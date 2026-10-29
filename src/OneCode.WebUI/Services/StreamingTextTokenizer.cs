namespace OneCode.WebUI.Services;

/// <summary>
/// 流式尾段的字级错峰支持：判定尾段是否为纯文本（Markdown 渲染结果与原始文本逐字一致），
/// 并把纯文本按字（CJK）/词（拉丁）拆分为 token，供 <c>MessageList</c> 逐 span 错峰入场。
/// 判定为非纯文本时必须回落整段 Markdown 渲染（块级动效），保证语义正确优先。
/// </summary>
public static class StreamingTextTokenizer
{
    /// <summary>
    /// 任一出现即视为非纯文本的字符：强调/行内代码/链接/转义/实体/自动链接前缀等
    /// 会让 Markdown 渲染结果偏离原始文本，走整段渲染才不会显示错误结构。
    /// </summary>
    private const string SyntaxChars = "`*_~[]<|\\&@^\t";

    /// <summary>尾段是否为纯文本：可按字符拆 span 原样渲染，无需 Markdown 解析。</summary>
    public static bool IsPlainText(string text)
    {
        if (text.Length == 0)
            return true;

        // \r 会被 Markdown 当行尾处理（软换行语义与原样输出不同），一律回落整段渲染。
        if (text.Contains('\r'))
            return false;

        foreach (var c in text)
        {
            if (SyntaxChars.Contains(c))
                return false;
        }

        // 空行 = 段落边界；双空格结尾 = 硬换行：两者 Markdown 结构与原样输出不同。
        if (text.Contains("\n\n") || text.Contains("  \n"))
            return false;

        // 自动链接：Markdig UseAutoLinks 会把裸 URL / www / mailto / tel / 邮箱转成链接。
        if (text.Contains("http", StringComparison.OrdinalIgnoreCase)
            || text.Contains("://", StringComparison.Ordinal)
            || text.Contains("www.", StringComparison.OrdinalIgnoreCase)
            || text.Contains("mailto", StringComparison.OrdinalIgnoreCase)
            || text.Contains("tel:", StringComparison.OrdinalIgnoreCase))
            return false;

        // 图片与 EmphasisExtras 的 Marked(==) / Inserted(++)。
        if (text.Contains("![") || text.Contains("==") || text.Contains("++"))
            return false;

        return HasPlainLinesOnly(text);
    }

    /// <summary>
    /// 把纯文本 run 拆成 token：CJK 逐字、拉丁按词；空白附着到前一个 token
    /// （开头空白自成 token），保证 <c>string.Concat</c>(tokens) 与原 run 逐字相同。
    /// </summary>
    public static IReadOnlyList<string> Tokenize(string run)
    {
        var tokens = new List<string>();
        var i = 0;
        while (i < run.Length)
        {
            int end;
            if (IsCjk(run[i]))
            {
                end = i + 1;
            }
            else if (char.IsWhiteSpace(run[i]))
            {
                // 开头空白自成 token，避免附着目标缺失时丢字。
                end = i;
                while (end < run.Length && char.IsWhiteSpace(run[end]))
                    end++;
                tokens.Add(run[i..end]);
                i = end;
                continue;
            }
            else
            {
                end = i;
                while (end < run.Length && !char.IsWhiteSpace(run[end]) && !IsCjk(run[end]))
                    end++;
            }

            // 尾随空白附着到当前 token：空白不跨 span 边界，避免 HTML 折叠造成丢空。
            var spanEnd = end;
            while (spanEnd < run.Length && char.IsWhiteSpace(run[spanEnd]))
                spanEnd++;
            tokens.Add(run[i..spanEnd]);
            i = spanEnd;
        }

        return tokens;
    }

    /// <summary>CJK 逐字拆分范围：部首/注音/假名/统一表意/兼容表意/全角形式。</summary>
    private static bool IsCjk(char c) =>
        (c >= '\u2E80' && c <= '\u9FFF') ||
        (c >= '\uF900' && c <= '\uFAFF') ||
        (c >= '\uFF00' && c <= '\uFF60');

    /// <summary>逐行检查行首结构触发符：标题、引用、列表、分隔线、缩进代码块、有序列表。</summary>
    private static bool HasPlainLinesOnly(string text)
    {
        var lineStart = true;
        var seenContent = false;
        var leadingSpaces = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (!lineStart)
            {
                if (c == '\n')
                {
                    lineStart = true;
                    leadingSpaces = 0;
                }
                continue;
            }

            if (c == ' ')
            {
                leadingSpaces++;
                continue;
            }

            // 空行（含仅有空白的行）：开头的空行 Markdown 会忽略，继续判下一行的行首；
            // 夹在内容之间的空行是段落边界，快速路径还原不出两个段落。
            if (c == '\n')
            {
                if (seenContent)
                    return false;

                leadingSpaces = 0;
                continue;
            }

            // 4 空格缩进或 Tab = 缩进代码块。
            if (leadingSpaces >= 4 || c == '\t')
                return false;

            // 标题 / 引用 / 无序列表 / 分隔线 / Setext 底线。
            if (c is '#' or '>' or '-' or '=' or '+')
                return false;

            if (char.IsDigit(c) && IsOrderedListMarker(text, i))
                return false;

            lineStart = false;
            seenContent = true;
            leadingSpaces = 0;
        }

        return true;
    }

    /// <summary>有序列表标记：数字串 + '.' 或 ')'，后接空白或行尾（空列表项同样渲染为列表）。</summary>
    private static bool IsOrderedListMarker(string text, int digitStart)
    {
        var end = digitStart;
        while (end < text.Length && char.IsDigit(text[end]))
            end++;

        if (end >= text.Length || text[end] is not ('.' or ')'))
            return false;

        return end + 1 >= text.Length || text[end + 1] is ' ' or '\t' or '\n';
    }
}
