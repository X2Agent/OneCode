using OneCode.App.Tools;

namespace OneCode.Tests;

public sealed class WebFetchToolHtmlToMarkdownTests
{
    [Fact]
    public void HtmlToMarkdown_PreCode_PreservesIndentation_WhileProseStillCollapses()
    {
        const string html =
            "<h1>Title</h1>" +
            "<p>Hello    world</p>" +
            "<pre><code>def foo():\n    return 1\n</code></pre>" +
            "<p>After</p>";

        var md = WebFetchTool.HtmlToMarkdown(html);

        md.Should().Contain("# Title");
        md.Should().Contain("Hello world");
        md.Should().NotContain("Hello    world");
        md.Should().Contain("```\ndef foo():\n    return 1\n```");
        md.Should().Contain("After");
    }

    [Fact]
    public void HtmlToMarkdown_PreWithTabs_KeepsTabIndentation()
    {
        const string html = "<pre><code>if a:\n\treturn\n</code></pre>";

        var md = WebFetchTool.HtmlToMarkdown(html);

        md.Should().Contain("```\nif a:\n\treturn\n```");
    }

    [Fact]
    public void HtmlToMarkdown_ProseOnly_CompressesSpacesAndBlankLines()
    {
        var md = WebFetchTool.HtmlToMarkdown("<p>a   b</p><p>c</p>");

        md.Should().Be("a b\n\nc");
    }
}
