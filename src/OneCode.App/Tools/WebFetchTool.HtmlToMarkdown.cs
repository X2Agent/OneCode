using System.Text;
using AngleSharp.Dom;

namespace OneCode.App.Tools;

/// <summary>
/// HTML→Markdown conversion helpers for WebFetchTool.
/// Based on AngleSharp DOM traversal — all methods are private static except
/// <see cref="HtmlToMarkdown"/> (internal for regression tests).
/// </summary>
public sealed partial class WebFetchTool
{
    // Markdown fenced-code opener/closer length (` ``` `).
    private const int MarkdownFenceLength = 3;

    internal static string HtmlToMarkdown(string html)
    {
        if (string.IsNullOrWhiteSpace(html))
            return string.Empty;

        var document = _htmlParser.ParseDocument(html);

        foreach (var element in document.QuerySelectorAll("script, style").ToList())
            element.Remove();

        var sb = new StringBuilder(html.Length);
        if (document.Body is not null)
        {
            foreach (var node in document.Body.ChildNodes)
                RenderNode(sb, node);
        }

        return NormalizeWhitespace(sb.ToString());
    }

    private static void RenderNode(StringBuilder sb, INode node)
    {
        switch (node)
        {
            case IText textNode:
                sb.Append(textNode.Text);
                break;

            case IElement element:
                RenderElement(sb, element);
                break;

            case IComment:
                break;
        }
    }

    private static void RenderElement(StringBuilder sb, IElement element)
    {
        var tagName = element.TagName.ToLowerInvariant();

        switch (tagName)
        {
            case "h1":
            case "h2":
            case "h3":
            case "h4":
            case "h5":
            case "h6":
                var level = int.Parse(tagName[1..], CultureInfo.InvariantCulture);
                sb.Append(new string('#', level)).Append(' ');
                RenderChildren(sb, element);
                sb.Append("\n\n");
                break;

            case "p":
                RenderChildren(sb, element);
                sb.Append("\n\n");
                break;

            case "a":
                var href = element.GetAttribute("href");
                var linkText = element.TextContent?.Trim() ?? string.Empty;
                if (!string.IsNullOrEmpty(href) && !string.IsNullOrEmpty(linkText))
                    sb.Append(CultureInfo.InvariantCulture, $"[{linkText}]({href})");
                else
                    sb.Append(linkText);
                break;

            case "strong":
            case "b":
                sb.Append("**");
                RenderChildren(sb, element);
                sb.Append("**");
                break;

            case "em":
            case "i":
                sb.Append('*');
                RenderChildren(sb, element);
                sb.Append('*');
                break;

            case "li":
                sb.Append("- ");
                RenderChildren(sb, element);
                sb.Append('\n');
                break;

            case "ul":
            case "ol":
                RenderChildren(sb, element);
                sb.Append('\n');
                break;

            case "pre":
                var codeChild = element.QuerySelector("code");
                var preContent = codeChild?.TextContent ?? element.TextContent;
                sb.Append("```\n").Append(preContent.TrimEnd('\n', '\r')).Append("\n```\n");
                break;

            case "code":
                sb.Append('`');
                sb.Append(element.TextContent);
                sb.Append('`');
                break;

            case "br":
                sb.Append('\n');
                break;

            case "hr":
                sb.Append("\n---\n");
                break;

            case "div":
            case "span":
            case "section":
            case "article":
            case "header":
            case "footer":
            case "main":
            case "nav":
            case "aside":
            case "blockquote":
                RenderChildren(sb, element);
                break;

            default:
                RenderChildren(sb, element);
                break;
        }
    }

    private static void RenderChildren(StringBuilder sb, IElement element)
    {
        foreach (var child in element.ChildNodes)
            RenderNode(sb, child);
    }

    /// <summary>
    /// Collapse runs of spaces/tabs to one space and runs of newlines to a blank line,
    /// but copy markdown fenced code blocks (<c>```</c>) verbatim so <c>&lt;pre&gt;</c>
    /// indentation survives.
    /// </summary>
    private static string NormalizeWhitespace(string text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        var sb = new StringBuilder(text.Length);
        var i = 0;
        var length = text.Length;

        while (i < length)
        {
            if (IsMarkdownFenceAt(text, i))
            {
                var end = FindMarkdownFenceEnd(text, i);
                if (end < 0)
                {
                    sb.Append(text.AsSpan(i));
                    break;
                }

                sb.Append(text.AsSpan(i, end - i));
                i = end;
                continue;
            }

            var ch = text[i];

            if (ch == ' ' || ch == '\t')
            {
                sb.Append(' ');
                while (i < length && (text[i] == ' ' || text[i] == '\t'))
                    i++;
                continue;
            }

            if (ch == '\n' || ch == '\r')
            {
                while (i < length && (text[i] == '\n' || text[i] == '\r'))
                    i++;
                sb.Append("\n\n");
                continue;
            }

            sb.Append(ch);
            i++;
        }

        return sb.ToString().Trim();
    }

    private static bool IsMarkdownFenceAt(string text, int i)
        => i + MarkdownFenceLength <= text.Length
           && text[i] == '`' && text[i + 1] == '`' && text[i + 2] == '`'
           && (i == 0 || text[i - 1] == '\n' || text[i - 1] == '\r');

    /// <summary>
    /// Index just past the closing fence line (including its trailing newline),
    /// or -1 if the opening fence is unclosed.
    /// </summary>
    private static int FindMarkdownFenceEnd(string text, int openingFence)
    {
        var i = SkipToNextLine(text, openingFence + MarkdownFenceLength);

        while (i < text.Length)
        {
            if (IsMarkdownFenceAt(text, i))
                return SkipToNextLine(text, i + MarkdownFenceLength);

            i++;
        }

        return -1;
    }

    private static int SkipToNextLine(string text, int i)
    {
        while (i < text.Length && text[i] != '\n' && text[i] != '\r')
            i++;
        if (i < text.Length && text[i] == '\r')
            i++;
        if (i < text.Length && text[i] == '\n')
            i++;
        return i;
    }
}
