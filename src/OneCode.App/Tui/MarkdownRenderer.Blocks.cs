using Markdig.Extensions.Tables;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace OneCode.App.Tui;

/// <summary>
/// Complex markdown block renderers for <see cref="MarkdownRenderer"/>:
/// fenced/indented code (with copy header and diff coloring), tables,
/// and (nested) lists with checkbox support. These stay as partials of the
/// main dispatcher because rendering is mutually recursive (nested blocks
/// call back into RenderBlock) and shares the inline-extraction helpers.
/// </summary>
internal static partial class MarkdownRenderer
{
    /// <summary>
    /// Renders a fenced code block with a beautified header bar:
    ///
    ///   ┌─ csharp ─────────────── [ copy ] ─┐
    ///   │ var x = 1;                         │
    ///   │ var y = 2;                         │
    ///   └────────────────────────────────────┘
    ///
    /// The header line carries a <see cref="CodeBlockCopyTag"/> so clicking it
    /// copies the code to the clipboard. The language label uses accent color,
    /// the "copy" text uses a distinct info color, and the code body uses a
    /// subtle background tint to visually separate it from surrounding text.
    /// </summary>
    private static void RenderFencedCode(FencedCodeBlock code, List<ConvLine> lines, int viewWidth)
    {
        var lang = code.Info ?? "";
        var codeLines = ExtractCodeLines(code);
        var codeText = string.Join("\n", codeLines);

        // Calculate inner width (space for code text between borders)
        var maxCodeWidth = 0;
        foreach (var ln in codeLines)
        {
            var w = TextWidthHelper.GetDisplayWidth(ln);
            if (w > maxCodeWidth) maxCodeWidth = w;
        }

        // Total block width (including 2-char indent) = innerWidth + 6:
        //   indent(2) + │(1) + space(1) + code(innerWidth) + space(1) + │(1)
        var maxInner = Math.Max(10, viewWidth - 6);

        // The copy button text: " [ copy ] " (10 chars display width)
        const string copyLabel = "copy";
        var headerCopyBtn = $" [ {copyLabel} ] ";

        // Header minimum inner width must fit the copy button plus minimal border decoration.
        // Layout: "  ┌─ " + [lang + " ─ "] + dashes + "[ copy ]" + " ─┐"
        var langDisplayWidth = TextWidthHelper.GetDisplayWidth(lang);
        var headerMinInner = string.IsNullOrEmpty(lang)
            ? TextWidthHelper.GetDisplayWidth(headerCopyBtn) + 2   // copy button + minimal dashes
            : langDisplayWidth + 3 + TextWidthHelper.GetDisplayWidth(headerCopyBtn) + 2;

        var innerWidth = Math.Max(maxCodeWidth, headerMinInner);
        if (innerWidth > maxInner) innerWidth = maxInner;

        // Target total display width (including indent) — body and header must match this
        var totalWidth = innerWidth + 6;

        // Build header parts
        var headerLeft = $"  {TuiGlyphs.BorderTopLeft}{TuiGlyphs.BorderHorizontal} ";
        var headerLang = string.IsNullOrEmpty(lang) ? "" : lang;
        var headerLangSep = string.IsNullOrEmpty(lang) ? "" : $" {TuiGlyphs.BorderHorizontal} ";
        var headerRight = $"{TuiGlyphs.BorderHorizontal}{TuiGlyphs.BorderTopRight}";

        // Compute display width of all fixed parts (everything except the filler dashes)
        var fixedDisplayWidth = TextWidthHelper.GetDisplayWidth(headerLeft)
                              + TextWidthHelper.GetDisplayWidth(headerLang)
                              + TextWidthHelper.GetDisplayWidth(headerLangSep)
                              + TextWidthHelper.GetDisplayWidth(headerCopyBtn)
                              + TextWidthHelper.GetDisplayWidth(headerRight);
        var dashCount = Math.Max(0, totalWidth - fixedDisplayWidth);
        var dashes = new string(TuiGlyphs.BorderHorizontal[0], dashCount);

        var headerText = $"{headerLeft}{headerLang}{headerLangSep}{dashes}{headerCopyBtn}{headerRight}";
        var headerSegments = new List<LineSegment>
        {
            new(headerLeft, TuiPalette.Border),
            new(headerLang, TuiPalette.Accent),
            new(headerLangSep + dashes, TuiPalette.Border),
            new(headerCopyBtn, TuiPalette.Info),
            new(headerRight, TuiPalette.Border),
        };
        lines.Add(new ConvLine(LineRole.System, headerText, headerSegments, new CodeBlockCopyTag(codeText)));

        // Code body lines
        foreach (var ln in codeLines)
        {
            var role = DetectDiffLineRole(ln);
            var displayWidth = TextWidthHelper.GetDisplayWidth(ln);
            var padding = Math.Max(0, innerWidth - displayWidth);
            var paddedLine = ln + new string(' ', padding);
            var codeColor = role switch
            {
                LineRole.DiffAdded => TuiPalette.DiffAdded,
                LineRole.DiffRemoved => TuiPalette.DiffRemoved,
                LineRole.DiffHunk => TuiPalette.DiffHunk,
                _ => TuiPalette.FgPrimary,
            };
            lines.Add(new ConvLine(role,
                $"  {TuiGlyphs.BorderVertical} {paddedLine} {TuiGlyphs.BorderVertical}",
                new[] {
                    new LineSegment($"  {TuiGlyphs.BorderVertical} ", TuiPalette.Border),
                    new LineSegment(paddedLine, codeColor),
                    new LineSegment($" {TuiGlyphs.BorderVertical}", TuiPalette.Border),
                }));
        }

        // Bottom border
        var bottomDashes = new string(TuiGlyphs.BorderHorizontal[0], innerWidth + 2);
        var bottomText = $"  {TuiGlyphs.BorderBottomLeft}{bottomDashes}{TuiGlyphs.BorderBottomRight}";
        lines.Add(new ConvLine(LineRole.System, bottomText,
            new[] { new LineSegment(bottomText, TuiPalette.Border) }));
    }

    private static void RenderCodeBlock(CodeBlock code, List<ConvLine> lines, int viewWidth)
    {
        // Indented code block (no language) — render with simple left border.
        var codeLines = ExtractCodeLines(code);
        var maxCodeWidth = 0;
        foreach (var ln in codeLines)
        {
            var w = TextWidthHelper.GetDisplayWidth(ln);
            if (w > maxCodeWidth) maxCodeWidth = w;
        }
        var maxInner = Math.Max(10, viewWidth - 6);
        var innerWidth = Math.Min(maxCodeWidth, maxInner);

        foreach (var ln in codeLines)
        {
            var role = DetectDiffLineRole(ln);
            var displayWidth = TextWidthHelper.GetDisplayWidth(ln);
            var padding = Math.Max(0, innerWidth - displayWidth);
            var paddedLine = ln + new string(' ', padding);
            var lineText = $"  {TuiGlyphs.BorderVertical} {paddedLine} {TuiGlyphs.BorderVertical}";
            var codeColor = role switch
            {
                LineRole.DiffAdded => TuiPalette.DiffAdded,
                LineRole.DiffRemoved => TuiPalette.DiffRemoved,
                LineRole.DiffHunk => TuiPalette.DiffHunk,
                _ => TuiPalette.FgPrimary,
            };
            lines.Add(new ConvLine(role, lineText,
                new[] { new LineSegment($"  {TuiGlyphs.BorderVertical} ", TuiPalette.Border),
                        new LineSegment(paddedLine, codeColor),
                        new LineSegment($" {TuiGlyphs.BorderVertical}", TuiPalette.Border) }));
        }
    }

    private static List<string> ExtractCodeLines(LeafBlock code)
    {
        var result = new List<string>();
        var lines = code.Lines;
        if (lines.Count == 0) return result;

        foreach (var line in lines)
        {
            var text = line.ToString();
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

        // Use display width (not char count) so CJK cells don't misalign columns.
        var colWidths = new int[colCount];
        foreach (TableRow? row in table)
        {
            for (var i = 0; i < row.Count && i < colCount; i++)
            {
                var cellText = ExtractTableCellText(row[i]);
                colWidths[i] = Math.Max(colWidths[i], TextWidthHelper.GetDisplayWidth(cellText));
            }
        }

        for (var i = 0; i < colWidths.Length; i++)
            colWidths[i] = Math.Min(colWidths[i], 40);

        // Layout: "  │ " + cells joined by " │ " + " │".
        // Total display width = 2 + sum(colWidths) + (colCount+1)*3.
        // Cap to viewWidth - 2 so the table fits within the terminal.
        var totalWidth = colWidths.Sum() + (colCount + 1) * 3 + 2;
        var maxTableWidth = Math.Max(20, viewWidth - 2);
        if (totalWidth > maxTableWidth)
        {
            var scale = (double)maxTableWidth / totalWidth;
            for (var i = 0; i < colWidths.Length; i++)
                colWidths[i] = Math.Max(4, (int)(colWidths[i] * scale));
        }

        var isFirst = true;
        foreach (TableRow? row in table)
        {
            var cells = new string[colCount];
            for (var i = 0; i < colCount; i++)
            {
                var text = i < row.Count ? ExtractTableCellText(row[i]) : "";
                var cellDisplayWidth = TextWidthHelper.GetDisplayWidth(text);
                if (cellDisplayWidth > colWidths[i])
                    text = TextWidthHelper.TruncateByWidth(text, colWidths[i] - 1) + TuiGlyphs.Ellipsis;
                // Pad using display-width-aware helper so CJK cells stay aligned.
                var padCount = Math.Max(0, colWidths[i] - TextWidthHelper.GetDisplayWidth(text));
                cells[i] = text + new string(' ', padCount);
            }

            lines.Add(new ConvLine(LineRole.System,
                $"  │ {string.Join(" │ ", cells)} │"));

            if (isFirst)
            {
                var sep = new string[colCount];
                for (var i = 0; i < colCount; i++)
                    sep[i] = new string('─', colWidths[i]);
                lines.Add(new ConvLine(LineRole.System,
                    $"  ├─{string.Join("─┼─", sep)}─┤"));
                isFirst = false;
            }
        }
    }

    private static string ExtractTableCellText(Block cell)
    {
        if (cell is ParagraphBlock para)
            return ExtractInlineText(para.Inline);

        var lines = new List<ConvLine>();
        RenderBlock(cell, lines, indent: 0);
        return string.Join(" ", lines.Select(l => l.Text));
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
        if (para?.Inline == null) return (false, false, "");

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
