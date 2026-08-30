using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace OneCode.App.Tui;

/// <summary>
/// Markdown → <see cref="ConvLine"/> renderer: block dispatch, inline text
/// extraction, and simple block types (heading / quote / paragraph / html).
/// Complex block types (code, table, list) live in
/// <c>MarkdownRenderer.Blocks.cs</c>.
/// </summary>
internal static partial class MarkdownRenderer
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .Build();

    public static List<ConvLine> Render(string markdown, int viewWidth = 80)
    {
        if (string.IsNullOrWhiteSpace(markdown))
            return [new ConvLine(LineRole.Assistant, "")];

        var doc = Markdig.Markdown.Parse(markdown, Pipeline);
        var lines = new List<ConvLine>();
        var effectiveWidth = viewWidth > 0 ? viewWidth : 80;

        foreach (var block in doc)
        {
            RenderBlock(block, lines, indent: 0, viewWidth: effectiveWidth);
        }

        return lines;
    }

    private static void RenderBlock(Block block, List<ConvLine> lines, int indent, int viewWidth = 80)
    {
        switch (block)
        {
            case HeadingBlock heading:
                RenderHeading(heading, lines, viewWidth);
                break;

            case FencedCodeBlock code:
                RenderFencedCode(code, lines, viewWidth);
                break;

            case CodeBlock code:
                RenderCodeBlock(code, lines, viewWidth);
                break;

            case QuoteBlock quote:
                RenderQuote(quote, lines, viewWidth);
                break;

            case ListBlock list:
                RenderList(list, lines, indent, viewWidth);
                break;

            case ListItemBlock listItem:
                RenderListItem(listItem, lines, indent, viewWidth: viewWidth);
                break;

            case ThematicBreakBlock:
                // 主题分隔线使用 TuiGlyphs 统一管理
                lines.Add(new ConvLine(LineRole.System, $"  {new string('─', 32)}"));
                break;

            case HtmlBlock html:
                RenderHtmlBlock(html, lines, viewWidth);
                break;

            case Table table:
                RenderTable(table, lines, viewWidth);
                break;

            case ParagraphBlock para:
                RenderParagraph(para, lines, indent, viewWidth);
                break;

            default:
                RenderLeafBlock(block, lines, indent, viewWidth);
                break;
        }
    }

    private static void RenderHeading(HeadingBlock heading, List<ConvLine> lines, int viewWidth)
    {
        var text = ExtractInlineText(heading.Inline);
        var prefix = heading.Level switch
        {
            1 => "  ",
            2 => "  ",
            _ => "  ",
        };
        var role = heading.Level <= 2 ? LineRole.System : LineRole.Assistant;
        // 标题与正文同规则按视口宽度换行——长标题直出会被绘制层裁掉尾部。
        var available = Math.Max(10, viewWidth - prefix.Length);
        foreach (var ln in WordWrap(text, maxWidth: available))
            lines.Add(new ConvLine(role, $"{prefix}{ln}"));
    }

    private static void RenderQuote(QuoteBlock quote, List<ConvLine> lines, int viewWidth)
    {
        // Quote prefix is "  │ " (4 display cols). Available text width = viewWidth - 4.
        var available = Math.Max(10, viewWidth - 4);
        foreach (var subBlock in quote)
        {
            if (subBlock is ParagraphBlock para)
            {
                var text = ExtractInlineText(para.Inline);
                var wrapped = WordWrap(text, maxWidth: available);
                foreach (var ln in wrapped)
                    lines.Add(new ConvLine(LineRole.System, $"  │ {ln}"));
            }
            else
            {
                RenderBlock(subBlock, lines, indent: 0, viewWidth: viewWidth);
            }
        }
    }

    private static void RenderParagraph(ParagraphBlock para, List<ConvLine> lines, int indent, int viewWidth)
    {
        var text = ExtractInlineText(para.Inline);
        var prefix = new string(' ', indent * 2 + 2);
        // Prefix display width = indent*2 + 2. Available = viewWidth - prefix.
        var available = Math.Max(10, viewWidth - (indent * 2 + 2));
        var wrapped = WordWrap(text, maxWidth: available);

        foreach (var ln in wrapped)
            lines.Add(new ConvLine(LineRole.Assistant, $"{prefix}{ln}"));
    }

    private static void RenderHtmlBlock(HtmlBlock html, List<ConvLine> lines, int viewWidth)
    {
        var slice = html.Lines;
        var text = slice.ToString();
        var available = Math.Max(10, viewWidth - 2);
        foreach (var ln in text.Replace("\r\n", "\n").Split('\n'))
        {
            if (string.IsNullOrEmpty(ln))
                continue;
            foreach (var wrapped in WordWrap(ln, maxWidth: available))
                lines.Add(new ConvLine(LineRole.Assistant, $"  {wrapped}"));
        }
    }

    private static void RenderLeafBlock(Block block, List<ConvLine> lines, int indent, int viewWidth)
    {
        if (block is LeafBlock leaf && leaf.Inline != null)
        {
            var text = ExtractInlineText(leaf.Inline);
            var prefix = new string(' ', indent * 2 + 2);
            var available = Math.Max(10, viewWidth - (indent * 2 + 2));
            var wrapped = WordWrap(text, maxWidth: available);
            foreach (var ln in wrapped)
                lines.Add(new ConvLine(LineRole.Assistant, $"{prefix}{ln}"));
        }
    }

    private static string ExtractInlineText(ContainerInline? inline)
    {
        if (inline == null) return "";

        var sb = new System.Text.StringBuilder();
        var child = inline.FirstChild;

        while (child != null)
        {
            switch (child)
            {
                case LiteralInline literal:
                    sb.Append(literal.Content.ToString());
                    break;

                case CodeInline code:
                    sb.Append(code.Content.ToString());
                    break;

                case EmphasisInline emphasis:
                    if (emphasis.DelimiterChar == '~')
                    {
                        var inner = ExtractInlineText(emphasis);
                        foreach (var c in inner)
                        {
                            sb.Append(c);
                            sb.Append('\u0336');
                        }
                    }
                    else
                    {
                        sb.Append(ExtractInlineText(emphasis));
                    }
                    break;

                case LinkInline link:
                    if (link.IsImage)
                    {
                        var alt = ExtractInlineText(link);
                        sb.Append(CultureInfo.InvariantCulture, $"[{alt}]");
                    }
                    else
                    {
                        sb.Append(ExtractInlineText(link));
                    }
                    break;

                case LineBreakInline:
                    sb.Append('\n');
                    break;

                case HtmlEntityInline entity:
                    sb.Append(entity.Transcoded.ToString());
                    break;

                case HtmlInline htmlInline:
                    sb.Append(htmlInline.Tag);
                    break;

                case AutolinkInline autolink:
                    sb.Append(autolink.Url);
                    break;

                default:
                    if (child is ContainerInline container)
                        sb.Append(ExtractInlineText(container));
                    break;
            }

            child = child.NextSibling;
        }

        return sb.ToString();
    }

    private static List<string> WordWrap(string text, int maxWidth)
    {
        // Delegate to TextWidthHelper.WordWrapByWidth so CJK / fullwidth /
        // emoji characters (display width 2) are measured correctly. The
        // previous implementation used char count, which let Chinese paragraphs
        // overflow the terminal width by ~50%.
        return TextWidthHelper.WordWrapByWidth(text, maxWidth);
    }
}
