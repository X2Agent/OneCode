using AngleSharp.Html.Dom;
using Ganss.Xss;
using Markdig;

namespace OneCode.WebUI.Services;

/// <summary>
/// 对话正文的 Markdown 渲染：Markdig 解析 → 白名单消毒 → 可安全插入 MarkupString 的 HTML。
/// 模型输出按不可信文本处理：原始 HTML 保留排版能力，但脚本、事件属性、危险协议一律剥离。
/// 整页 HTML 产物不走这里（不允许进入主文档），见 <see cref="HtmlArtifactSplitter"/> + sandbox iframe。
/// </summary>
public sealed class MarkdownRenderer
{
    // 排版类 CSS：只放行视觉属性，避免 margin/position/z-index 之类破坏页面布局。
    private static readonly string[] AllowedCssProperties =
    [
        "color",
        "background-color",
        "font-weight",
        "font-style",
        "font-size",
        "line-height",
        "letter-spacing",
        "text-align",
        "text-decoration",
        "vertical-align",
    ];

    private static readonly string[] RemovedTags =
    [
        "button", "input", "textarea", "select", "option", "optgroup", "form", "fieldset", "label",
        "dialog", "audio", "video", "source", "track", "picture", "map", "area", "datalist",
        "progress", "output", "dir", "font", "center", "big", "tt", "strike", "acronym", "iframe",
    ];

    private readonly MarkdownPipeline _pipeline = new MarkdownPipelineBuilder()
        .UseEmphasisExtras()
        .UseAutoLinks()
        .UsePipeTables()
        .Build();

    private readonly HtmlSanitizer _sanitizer = CreateSanitizer();

    // 消毒器实例跨电路共享，串行调用。
    private readonly Lock _sanitizeGate = new();

    /// <summary>把 Markdown 文本渲染为可安全插入的 HTML 片段。</summary>
    public string ToHtml(string? markdown)
    {
        if (string.IsNullOrEmpty(markdown))
            return string.Empty;

        var html = Markdown.ToHtml(markdown, _pipeline);
        lock (_sanitizeGate)
            return _sanitizer.Sanitize(html);
    }

    private static HtmlSanitizer CreateSanitizer()
    {
        var sanitizer = new HtmlSanitizer();

        // 外链图片：允许 https/http 直连（离线时裂图由 alt 文本兜底）。
        sanitizer.AllowedTags.Add("img");
        sanitizer.AllowedAttributes.Add("alt");
        sanitizer.AllowedAttributes.Add("width");
        sanitizer.AllowedAttributes.Add("height");

        // 行内 style：仅放行排版属性。
        sanitizer.AllowedAttributes.Add("style");
        foreach (var property in AllowedCssProperties)
            sanitizer.AllowedCssProperties.Add(property);

        // class/id 会让模型输出命中宿主页面的样式与锚点，直接不放行。
        sanitizer.AllowedAttributes.Remove("class");
        sanitizer.AllowedAttributes.Remove("id");

        // 正文只需要排版能力：表单控件、媒体、内嵌文档一律不渲染。
        foreach (var tag in RemovedTags)
            sanitizer.AllowedTags.Remove(tag);

        // 链接点击不得把 SPA 导航走：统一新窗口打开并断掉 opener。
        sanitizer.PostProcessNode += (_, e) =>
        {
            if (e.Node is not IHtmlAnchorElement anchor)
                return;

            anchor.SetAttribute("target", "_blank");
            anchor.SetAttribute("rel", "noopener noreferrer nofollow");
        };

        return sanitizer;
    }
}
