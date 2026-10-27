using System.Text;

namespace OneCode.WebUI.Services;

/// <summary>
/// 助手消息的一段：普通 Markdown 文本，或一个整页 HTML 产物。
/// 产物段的 <see cref="Markdown"/> 是围栏原文（供「查看源码」），<see cref="ArtifactHtml"/> 是文档正文。
/// </summary>
public sealed record MarkdownSegment(string Markdown, string? ArtifactHtml)
{
    public bool IsArtifact => ArtifactHtml is not null;
}

/// <summary>
/// 从助手消息里抽取整页 HTML 产物：只有「html/htm 围栏 + 含 &lt;html 或 &lt;!doctype」的代码块才抽出，
/// 其余围栏（片段、SVG、其它语言）保持原样交给 Markdown 渲染。
/// 整页 HTML 不进主文档——它由 <c>HtmlArtifactCard</c> 的 sandbox iframe 承载。
/// </summary>
public static class HtmlArtifactSplitter
{
    private static readonly string[] HtmlLanguages = ["html", "htm"];

    public static IReadOnlyList<MarkdownSegment> Split(string content)
    {
        if (string.IsNullOrEmpty(content))
            return [];

        var segments = new List<MarkdownSegment>();
        var markdown = new StringBuilder();
        var fence = new StringBuilder();
        var body = new StringBuilder();
        string? language = null;

        foreach (var rawLine in content.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            var trimmed = line.TrimStart();

            if (language is null)
            {
                if (!trimmed.StartsWith("```", StringComparison.Ordinal))
                {
                    markdown.AppendLine(line);
                    continue;
                }

                language = trimmed[3..].Trim();
                fence.Clear().AppendLine(line);
                body.Clear();
                continue;
            }

            fence.AppendLine(line);
            if (!trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                body.AppendLine(line);
                continue;
            }

            if (IsFullDocument(language, body))
            {
                FlushMarkdown(segments, markdown);
                segments.Add(new MarkdownSegment(fence.ToString().Trim(), body.ToString().Trim()));
            }
            else
            {
                // 非产物：整段围栏并回 Markdown 原文。
                markdown.Append(fence);
            }

            language = null;
        }

        // 未闭合围栏（流式输出中途）：当前围栏按 Markdown 原文处理，等消息完成后再判定。
        if (language is not null)
            markdown.Append(fence);

        FlushMarkdown(segments, markdown);

        return segments.Count == 0
            ? [new MarkdownSegment(content, null)]
            : segments;
    }

    private static void FlushMarkdown(List<MarkdownSegment> segments, StringBuilder markdown)
    {
        var text = markdown.ToString().Trim();
        markdown.Clear();

        if (text.Length > 0)
            segments.Add(new MarkdownSegment(text, null));
    }

    private static bool IsFullDocument(string language, StringBuilder body)
        => HtmlLanguages.Contains(language.ToLowerInvariant()) && LooksLikeDocument(body.ToString());

    private static bool LooksLikeDocument(string html)
        => html.Contains("<html", StringComparison.OrdinalIgnoreCase)
            || html.Contains("<!doctype html", StringComparison.OrdinalIgnoreCase);
}
