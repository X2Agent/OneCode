using OneCode.App.Tools;
using OneCode.Infrastructure.Text;

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

        var md = HtmlToMarkdownConverter.Convert(html);

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

        var md = HtmlToMarkdownConverter.Convert(html);

        md.Should().Contain("```\nif a:\n\treturn\n```");
    }

    [Fact]
    public void HtmlToMarkdown_ProseOnly_CompressesSpacesAndBlankLines()
    {
        var md = HtmlToMarkdownConverter.Convert("<p>a   b</p><p>c</p>");

        md.Should().Be("a b\n\nc");
    }

    [Fact]
    public void HtmlToMarkdown_StripsSiteChrome_NavAsideAndFooter()
    {
        // 站点外壳（导航、侧栏、页脚、全站菜单）不携带正文信息，却会成段混进
        // Markdown 结果——既污染工具展示，也白白消耗模型上下文。
        const string html = """
            <html><body>
                <nav><a href="/home">首页</a><a href="/news">新闻</a></nav>
                <aside>相关推荐</aside>
                <p>正文段落。</p>
                <footer>版权所有 © 示例站点</footer>
            </body></html>
            """;

        var md = HtmlToMarkdownConverter.Convert(html);

        md.Should().Contain("正文段落");
        md.Should().NotContain("首页").And.NotContain("新闻")
            .And.NotContain("相关推荐").And.NotContain("版权所有");
    }

    [Fact]
    public void HtmlToMarkdown_MainContentPresent_RendersOnlyMain()
    {
        // 页面声明了主内容区时只渲染 main：侧栏链接列表等剩余外壳一并丢弃。
        const string html = """
            <html><body>
                <div class="sidebar"><a href="/a">侧栏链接</a></div>
                <main><h1>文章标题</h1><p>文章正文。</p></main>
            </body></html>
            """;

        var md = HtmlToMarkdownConverter.Convert(html);

        md.Should().Contain("# 文章标题").And.Contain("文章正文");
        md.Should().NotContain("侧栏链接");
    }

    [Fact]
    public void HtmlToMarkdown_HeaderInsideArticle_IsKept()
    {
        // 文章内部的 header/footer（如摘要头）是有语义的正文结构，保留。
        const string html = """
            <article>
                <header><h1>文章标题</h1></header>
                <p>文章正文。</p>
            </article>
            """;

        var md = HtmlToMarkdownConverter.Convert(html);

        md.Should().Contain("文章标题").And.Contain("文章正文");
    }

    [Fact]
    public void HtmlToMarkdown_NoscriptOnlyShell_KeepsDegradationHint()
    {
        // JS-only SPA 外壳唯一的正文是 noscript 降级提示——剥掉它 WebFetch 会返回
        // 空正文，模型就失去「该改用 BrowserFetch」的判断依据（WebFetchToolJsHintTests
        // 的产品契约）。有 main/article 的页面走主内容提取，自然不会渲染到 noscript。
        const string html = "<html><body>" +
            "<noscript>You need to enable JavaScript to run this app.</noscript>" +
            "<div id=\"root\"></div></body></html>";

        var md = HtmlToMarkdownConverter.Convert(html);

        md.Should().Contain("enable JavaScript");
    }

    [Fact]
    public void HtmlToMarkdown_SiblingArticlesNoMain_RendersAllListEntries()
    {
        // 列表页常见结构：多个同级 article 平铺在 body 下（无 main 容器）。
        // 只取第一个命中会丢掉其余条目——搜索/列表结果会静默缺页。
        const string html = """
            <html><body>
                <nav>站点导航</nav>
                <article><h2>条目一</h2><p>第一条内容。</p></article>
                <article><h2>条目二</h2><p>第二条内容。</p></article>
                <article><h2>条目三</h2><p>第三条内容。</p></article>
            </body></html>
            """;

        var md = HtmlToMarkdownConverter.Convert(html);

        md.Should().Contain("条目一").And.Contain("条目二").And.Contain("条目三");
        md.Should().NotContain("站点导航");
    }

    [Fact]
    public void HtmlToMarkdown_MultipleArticlesInsideMain_RenderOnceViaContainer()
    {
        // 有 main 容器时列表条目是其后代，随容器一次性渲染；
        // 按顶层 article 再叠加渲染会造成整段重复。
        const string html = """
            <html><body>
                <main>
                    <article><h2>条目一</h2></article>
                    <article><h2>条目二</h2></article>
                </main>
                <aside>推荐阅读</aside>
            </body></html>
            """;

        var md = HtmlToMarkdownConverter.Convert(html);

        md.Should().Contain("条目一").And.Contain("条目二");
        md.Should().NotContain("推荐阅读");
        md.Should().Contain("条目一", Exactly.Once());
    }

    [Fact]
    public void HtmlToMarkdown_NestedArticleInsideArticle_RendersInnerOnlyOnce()
    {
        // article 嵌套（如评论嵌套楼中楼）：外层已随顶层渲染，内层不得再次渲染。
        const string html = """
            <html><body>
                <article>
                    <h1>正文标题</h1>
                    <article><p>嵌套评论。</p></article>
                </article>
            </body></html>
            """;

        var md = HtmlToMarkdownConverter.Convert(html);

        md.Should().Contain("正文标题").And.Contain("嵌套评论");
        md.Should().Contain("嵌套评论", Exactly.Once());
    }

    [Theory]
    [InlineData("<meta charset=\"gb2312\">", null)]
    [InlineData("<meta http-equiv=\"Content-Type\" content=\"text/html; charset=gbk\" />", null)]
    [InlineData("<meta charset='GB18030'>", "iso-8859-1")]
    [InlineData("", "gbk")]
    public void DecodeResponseBytes_ChineseGbEncodings_DecodesWithoutReplacementCharacters(
        string metaTag,
        string? headerCharset)
    {
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
        var gbk = System.Text.Encoding.GetEncoding("GB18030");
        var html = $"<html><head>{metaTag}</head><body><h1>孙宇晨最新动态与币安时间线</h1><p>波场 TRON 生态新闻汇总。</p></body></html>";
        var bytes = gbk.GetBytes(html);

        var decoded = WebFetchTool.DecodeResponseBytes(bytes, headerCharset);
        var markdown = HtmlToMarkdownConverter.Convert(decoded);

        decoded.Should().NotContain("\uFFFD");
        markdown.Should().Contain("孙宇晨最新动态与币安时间线");
        markdown.Should().Contain("波场 TRON 生态新闻汇总");
    }
}
