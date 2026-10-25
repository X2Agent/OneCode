using Markdig;

namespace OneCode.WebUI.Services;

/// <summary>
/// 对话正文的 Markdown 渲染。原始 HTML 一律禁用：
/// 模型输出与工具结果都按不可信文本处理，不得透传为可执行标记。
/// </summary>
public sealed class MarkdownRenderer
{
    private readonly MarkdownPipeline _pipeline = new MarkdownPipelineBuilder()
        .DisableHtml()
        .UseEmphasisExtras()
        .UseAutoLinks()
        .Build();

    /// <summary>把 Markdown 文本渲染为可安全插入的 HTML 片段。</summary>
    public string ToHtml(string? markdown)
        => string.IsNullOrEmpty(markdown) ? string.Empty : Markdown.ToHtml(markdown, _pipeline);
}
