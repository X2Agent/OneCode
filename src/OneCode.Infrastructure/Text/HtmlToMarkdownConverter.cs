using AngleSharp.Dom;
using AngleSharp.Html.Parser;

namespace OneCode.Infrastructure.Text;

/// <summary>
/// HTML→Markdown 转换器（WebFetchTool 专用）——基于 AngleSharp DOM 遍历的纯静态实现。
/// </summary>
public static class HtmlToMarkdownConverter
{
    private static readonly HtmlParser Parser = new();

    // Markdown fenced-code opener/closer length (` ``` `).
    private const int MarkdownFenceLength = 3;

    public static string Convert(string html)
    {
        if (string.IsNullOrWhiteSpace(html))
            return string.Empty;

        var document = Parser.ParseDocument(html);

        // 剥离脚本/样式与页面外壳（导航、页眉页脚、表单等）：这些是站点 chrome，
        // 不携带正文信息，却会成段混进 Markdown 结果——既污染对话流里的工具展示，
        // 也白白消耗模型上下文。文章内部的 header/footer（如摘要头）保留。
        // noscript 不剥：JS-only 页面（SPA 外壳）唯一的正文就是 noscript 的降级提示，
        // 剥掉会让 WebFetch 返回空正文；有 main/article 的页面自然不会渲染到它。
        foreach (var element in document.QuerySelectorAll(
            """
            script, style, nav, aside, form, svg, iframe,
            [role='navigation'], [role='banner'], [role='contentinfo'], [aria-hidden='true']
            """).ToList())
        {
            element.Remove();
        }
        foreach (var element in document.QuerySelectorAll("header, footer").ToList())
        {
            if (!element.IsRootedInMainContent())
                element.Remove();
        }

        // 页面声明了主内容区（main / [role='main']）时只渲染该容器——列表页的多个
        // article 是其后代，随容器一并渲染。无 main 容器时收集顶层 article（列表页
        // 常见结构：多个同级 article 平铺在 body 下，只取第一个命中会丢掉其余条目）
        // 全部渲染；两者皆无时回落 Body。被 chrome 剥离的节点 ParentElement 为 null，
        // 一并跳过。
        var sb = new StringBuilder(html.Length);

        var mainContainer = document.QuerySelector("main, [role='main']");
        if (mainContainer?.ParentElement is not null)
        {
            RenderElement(sb, mainContainer);
        }
        else
        {
            var topLevelArticles = document.QuerySelectorAll("article")
                .Where(article => article.ParentElement is not null && !IsNestedInAnotherArticle(article))
                .ToList();

            if (topLevelArticles.Count > 0)
            {
                foreach (var article in topLevelArticles)
                    RenderElement(sb, article);
            }
            else if (document.Body is not null)
            {
                foreach (var node in document.Body.ChildNodes)
                    RenderNode(sb, node);
            }
        }

        return NormalizeWhitespace(sb.ToString());
    }

    /// <summary>article 是否嵌套在另一个 article 内（外层已渲染，内层不得重复）。</summary>
    private static bool IsNestedInAnotherArticle(IElement element)
    {
        for (var parent = element.ParentElement; parent is not null; parent = parent.ParentElement)
        {
            if (parent.TagName.Equals("article", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
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

            case "blockquote":
                RenderChildren(sb, element);
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

    /// <summary>元素是否位于 main/article 之内——文章内部的 header/footer 属于正文，不剥离。</summary>
    private static bool IsRootedInMainContent(this IElement element)
    {
        for (var current = element.ParentElement; current is not null; current = current.ParentElement)
        {
            var tag = current.TagName.ToLowerInvariant();
            if (tag is "main" or "article" || current.GetAttribute("role") == "main")
                return true;
        }
        return false;
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
