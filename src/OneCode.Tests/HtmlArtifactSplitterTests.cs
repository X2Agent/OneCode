using OneCode.WebUI.Services;

namespace OneCode.Tests;

/// <summary>
/// 整页 HTML 产物抽取：只有「html/htm 围栏 + 含 &lt;html 或 &lt;!doctype」的代码块才离开主文档进沙箱预览，
/// 其余围栏必须原样留在 Markdown 里（否则片段会被误判、代码会丢失）。
/// </summary>
public sealed class HtmlArtifactSplitterTests
{
    [Fact]
    public void Split_FullDocumentFence_ExtractsArtifactWithSurroundingText()
    {
        const string content = "下面是一个时间线页面：\n\n```html\n<!DOCTYPE html>\n<html><body>hi</body></html>\n```\n\n共 3 条数据。";

        var segments = HtmlArtifactSplitter.Split(content);

        segments.Should().HaveCount(3);
        segments[0].IsArtifact.Should().BeFalse();
        segments[0].Markdown.Should().Contain("时间线页面");

        segments[1].IsArtifact.Should().BeTrue();
        segments[1].ArtifactHtml.Should().Contain("<html>");
        segments[1].Markdown.Should().Contain("```html", "产物段保留围栏原文供查看源码");

        segments[2].IsArtifact.Should().BeFalse();
        segments[2].Markdown.Should().Contain("共 3 条数据");
    }

    [Fact]
    public void Split_HtmlFragmentFence_StaysMarkdown()
    {
        const string content = "```html\n<div class=\"card\">片段</div>\n```";

        var segments = HtmlArtifactSplitter.Split(content);

        segments.Should().ContainSingle();
        segments[0].IsArtifact.Should().BeFalse();
        segments[0].Markdown.Should().Contain("```html");
        segments[0].Markdown.Should().Contain("片段");
    }

    [Fact]
    public void Split_NonHtmlLanguageContainingHtmlMarker_StaysMarkdown()
    {
        const string content = "```js\nconst tpl = \"<html>\";\n```";

        var segments = HtmlArtifactSplitter.Split(content);

        segments.Should().ContainSingle();
        segments[0].IsArtifact.Should().BeFalse();
    }

    [Fact]
    public void Split_UnclosedFence_StaysMarkdown()
    {
        // 流式输出中途：围栏还没闭合，此时不能当成产物（否则预览会闪一帧半成品）
        const string content = "```html\n<!DOCTYPE html>\n<html><body>写入中";

        var segments = HtmlArtifactSplitter.Split(content);

        segments.Should().ContainSingle();
        segments[0].IsArtifact.Should().BeFalse();
        segments[0].Markdown.Should().Contain("```html");
    }

    [Fact]
    public void Split_NoFence_ReturnsWholeContent()
    {
        var segments = HtmlArtifactSplitter.Split("普通的一段说明");

        segments.Should().ContainSingle();
        segments[0].Markdown.Should().Be("普通的一段说明");
    }
}
