using Markdig.Extensions.Tables;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace OneCode.Tui;

/// <summary>
/// <see cref="MarkdownRenderer"/> 的复杂块渲染器：围栏/缩进代码块（含 diff 着色）、
/// 表格以及（支持复选框的）嵌套列表。
/// </summary>
internal static partial class MarkdownRenderer
{
    /// <summary>
    /// 渲染围栏代码块：语言标签行 + 单侧 gutter 边线。
    ///
    ///   csharp
    ///   │ var x = 1;
    ///   │ var y = 2;
    ///
    /// 单侧边线不依赖左右对称，超宽行换行后仍与 gutter 对齐；语言标签行也不再需要
    /// 按宽度摊平横线（旧实现里 dashCount 在长 lang 下会算成 0，表头比底边短一截）。
    /// </summary>
    private static void RenderFencedCode(FencedCodeBlock code, List<ConvLine> lines, int viewWidth)
    {
        var lang = code.Info ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(lang))
        {
            lines.Add(new ConvLine(LineRole.System, $"  {lang}", [
                new LineSegment("  ", TuiPalette.Border),
                new LineSegment(lang, TuiPalette.Accent),
            ]));
        }

        RenderCodeLines(ExtractCodeLines(code), lines, viewWidth);
    }

    private static void RenderCodeBlock(CodeBlock code, List<ConvLine> lines, int viewWidth)
        => RenderCodeLines(ExtractCodeLines(code), lines, viewWidth);

    /// <summary>代码正文：每行前置 <c>│ </c> gutter，超宽行按可用宽度换行后仍然对齐。</summary>
    private static void RenderCodeLines(List<string> codeLines, List<ConvLine> lines, int viewWidth)
    {
        const int gutterWidth = 4; // "  " + "│" + " "
        var innerWidth = Math.Max(10, viewWidth - gutterWidth);

        foreach (var ln in codeLines)
        {
            var role = DetectDiffLineRole(ln);
            var codeColor = role switch
            {
                LineRole.DiffAdded => TuiPalette.DiffAdded,
                LineRole.DiffRemoved => TuiPalette.DiffRemoved,
                LineRole.DiffHunk => TuiPalette.DiffHunk,
                _ => TuiPalette.FgPrimary,
            };

            var fragments = TextWidthHelper.WordWrapByWidth(ln, innerWidth);
            if (fragments.Count == 0)
                fragments = [""];

            foreach (var fragment in fragments)
            {
                lines.Add(new ConvLine(role, $"  {TuiGlyphs.BorderVertical} {fragment}", [
                    new LineSegment($"  {TuiGlyphs.BorderVertical} ", TuiPalette.Border),
                    new LineSegment(fragment, codeColor),
                ]));
            }
        }
    }

    private static List<string> ExtractCodeLines(LeafBlock code)
    {
        List<string> result = [];
        var lines = code.Lines;
        if (lines.Count == 0) return result;

        foreach (var line in lines)
        {
            var text = line.ToString() ?? string.Empty;
            if (text.EndsWith('\n')) text = text[..^1];
            if (text.EndsWith('\r')) text = text[..^1];
            result.Add(text);
        }

        if (result.Count > 0 && result[^1].Length == 0)
            result.RemoveAt(result.Count - 1);

        return result;
    }

    private static LineRole DetectDiffLineRole(string line)
    {
        if (line.StartsWith("+++", StringComparison.Ordinal) || line.StartsWith("---", StringComparison.Ordinal))
            return LineRole.DiffHunk;
        if (line.StartsWith("@@", StringComparison.Ordinal))
            return LineRole.DiffHunk;
        if (line.StartsWith("+", StringComparison.Ordinal))
            return LineRole.DiffAdded;
        if (line.StartsWith("-", StringComparison.Ordinal))
            return LineRole.DiffRemoved;
        return LineRole.Assistant;
    }

    private static void RenderTable(Table table, List<ConvLine> lines, int viewWidth)
    {
        if (table.Count == 0) return;

        var colCount = table.ColumnDefinitions.Count;
        if (colCount == 0 && table.FirstOrDefault() is TableRow firstRow)
            colCount = firstRow.Count;

        if (colCount == 0) return;

        // 网格表格要求每列等宽、格内折行、CJK 与 ASCII 混排仍不错位——三个前提在终端里
        // 很难同时成立，等比压缩后每列只剩 4-5 列、内容全成省略号。统一走记录式：
        // 每行一条记录，列名做前缀，任意宽度都可读。
        RenderTableAsRecords(table, ExtractHeaders(table, colCount), lines, viewWidth);
    }

    private static string ExtractTableCellText(Block cell)
    {
        // Markdig 的 TableCell 是 ContainerBlock——单元格内容是其子块
        // （管道表格中通常是单个 ParagraphBlock），而非 cell 本身的 Inline。
        // 旧实现直接在 cell 上取 Inline，永远返回空串（表格渲染全空的根因）。
        if (cell is not ContainerBlock container)
            return cell is LeafBlock leaf ? ExtractInlineText(leaf.Inline) : "";

        List<string> parts = [];
        foreach (var sub in container)
        {
            var text = sub switch
            {
                ParagraphBlock para => ExtractInlineText(para.Inline),
                LeafBlock leaf when leaf.Inline is not null => ExtractInlineText(leaf.Inline),
                _ => "",
            };
            if (!string.IsNullOrEmpty(text))
                parts.Add(text);
        }
        return string.Join(" ", parts);
    }

    private static string[] ExtractHeaders(Table table, int colCount)
    {
        var headers = new string[colCount];
        if (table.FirstOrDefault() is TableRow headerRow)
        {
            for (var i = 0; i < headerRow.Count && i < colCount; i++)
                headers[i] = ExtractTableCellText(headerRow[i]);
        }
        for (var i = 0; i < colCount; i++)
            headers[i] = string.IsNullOrWhiteSpace(headers[i]) ? $"列{i + 1}" : headers[i];
        return headers;
    }

    /// <summary>
    /// 记录式表格渲染（窄屏）：
    ///
    ///   ── 第 2 行 ──
    ///   列A │ 值1（超宽自动换行，缩进对齐）
    ///   列B │ 值2
    ///
    /// 首行视为表头。每个单元格值按剩余宽度换行，不截断。
    /// </summary>
    private static void RenderTableAsRecords(Table table, string[] headers, List<ConvLine> lines, int viewWidth)
    {
        var rowIndex = 0;
        foreach (var row in table.OfType<TableRow>())
        {
            rowIndex++;
            lines.Add(new ConvLine(LineRole.System, $"  ── 第 {rowIndex} 行 ──"));

            for (var i = 0; i < headers.Length; i++)
            {
                var value = i < row.Count ? ExtractTableCellText(row[i]) : "";
                // 布局："  " + header + " │ " + wrapped value。首行宽度 = 2 + header宽 + 3。
                var label = $"{headers[i]} │ ";
                var labelWidth = 2 + TextWidthHelper.GetDisplayWidth(label);
                var available = Math.Max(10, viewWidth - labelWidth);
                var wrapped = TextWidthHelper.WordWrapByWidth(value, maxWidth: available);

                if (wrapped.Count == 0)
                {
                    lines.Add(new ConvLine(LineRole.System, $"  {label}"));
                    continue;
                }
                for (var w = 0; w < wrapped.Count; w++)
                {
                    lines.Add(w == 0
                        ? new ConvLine(LineRole.System, $"  {label}{wrapped[w]}")
                        : new ConvLine(LineRole.System, $"  {new string(' ', TextWidthHelper.GetDisplayWidth(label))}{wrapped[w]}"));
                }
            }
        }
    }

    private static void RenderList(ListBlock list, List<ConvLine> lines, int indent, int viewWidth = 80)
    {
        var ordered = list.IsOrdered;
        var itemIndex = 0;

        foreach (var item in list)
        {
            if (item is ListItemBlock listItem)
            {
                itemIndex++;
                RenderListItem(listItem, lines, indent, ordered, itemIndex, viewWidth);
            }
        }
    }

    private static void RenderListItem(ListItemBlock listItem, List<ConvLine> lines, int indent, bool ordered = false, int index = 0, int viewWidth = 80)
    {
        var bullet = ordered ? $"{index}." : "•";
        var itemIndent = new string(' ', indent * 2);
        var first = true;

        // Layout: "  " + itemIndent + bullet + " " + text. Available width for text:
        //   viewWidth - 2 (leading) - indent*2 - bullet display width - 1 (space after bullet)
        var prefixDisplayWidth = 2 + indent * 2 + TextWidthHelper.GetDisplayWidth(bullet) + 1;
        var availableWidth = Math.Max(10, viewWidth - prefixDisplayWidth);

        var firstPara = listItem.FirstOrDefault(b => b is ParagraphBlock) as ParagraphBlock;
        var (isCheckbox, isChecked, _) = DetectCheckbox(firstPara);

        foreach (var subBlock in listItem)
        {
            if (subBlock is ParagraphBlock para)
            {
                var text = ExtractInlineText(para.Inline);
                var isFirstPara = para == firstPara;

                if (isFirstPara && isCheckbox)
                {
                    var checkbox = isChecked ? "[x]" : "[ ]";
                    if (text.StartsWith("[ ] ", StringComparison.Ordinal))
                        text = text[4..];
                    else if (text.StartsWith("[x] ", StringComparison.Ordinal) || text.StartsWith("[X] ", StringComparison.Ordinal))
                        text = text[4..];
                    else if (text.StartsWith("[-] ", StringComparison.Ordinal))
                        text = text[4..];

                    // Checkbox adds "[x] " (4 cols) before the text.
                    var checkboxPrefix = prefixDisplayWidth + 4;
                    var checkAvailable = Math.Max(10, viewWidth - checkboxPrefix);
                    var wrapped = WordWrap(text, maxWidth: checkAvailable);

                    for (var i = 0; i < wrapped.Count; i++)
                    {
                        if (i == 0)
                        {
                            lines.Add(new ConvLine(LineRole.Assistant,
                                $"  {itemIndent}{checkbox} {wrapped[i]}"));
                        }
                        else
                        {
                            lines.Add(new ConvLine(LineRole.Assistant,
                                $"  {itemIndent}    {wrapped[i]}"));
                        }
                    }
                    first = false;
                    continue;
                }

                var normalWrapped = WordWrap(text, maxWidth: availableWidth);

                for (var i = 0; i < normalWrapped.Count; i++)
                {
                    if (first && i == 0)
                    {
                        lines.Add(new ConvLine(LineRole.Assistant, $"  {itemIndent}{bullet} {normalWrapped[i]}"));
                        first = false;
                    }
                    else
                    {
                        lines.Add(new ConvLine(LineRole.Assistant, $"  {itemIndent}  {normalWrapped[i]}"));
                    }
                }
            }
            else if (subBlock is ListBlock nestedList)
            {
                RenderList(nestedList, lines, indent + 1, viewWidth);
            }
            else
            {
                RenderBlock(subBlock, lines, indent + 1, viewWidth);
            }
        }
    }

    private static (bool isCheckbox, bool isChecked, string text) DetectCheckbox(ParagraphBlock? para)
    {
        if (para?.Inline is null) return (false, false, "");

        var firstChild = para.Inline.FirstChild;
        if (firstChild is not LiteralInline literal)
            return (false, false, "");

        var content = literal.Content.ToString();
        if (content.StartsWith("[ ] ", StringComparison.Ordinal))
            return (true, false, content);
        if (content.StartsWith("[x] ", StringComparison.Ordinal) || content.StartsWith("[X] ", StringComparison.Ordinal))
            return (true, true, content);

        return (false, false, content);
    }
}
