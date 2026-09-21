using NSubstitute;
using OneCode.Infrastructure.Search;

namespace OneCode.Tests;

/// <summary>
/// DuckDuckGo 提供方的解析契约（S3：适配已迁入 Infrastructure）。
/// </summary>
/// <remarks>
/// 这些用例随代码一起从 <c>WebSearchToolTests</c> 迁出：解析逻辑现在属于提供方适配，
/// 不再是搜索工具的私有 helper。
/// </remarks>
public sealed class DuckDuckGoSearchProviderTests
{
    [Fact]
    public void ParseResults_ValidHtml_ExtractsAllFields()
    {
        const string html = """
            <html><body>
            <div class="result">
              <a class="result__a" href="https://duckduckgo.com/l/?uddg=https%3A%2F%2Fexample.com%2Fpage">Example &amp; Co</a>
              <a class="result__snippet">A snippet about the page.</a>
            </div>
            </body></html>
            """;

        var hits = DuckDuckGoSearchProvider.ParseResults(html, maxResults: 8);

        hits.Should().ContainSingle();
        hits[0].Title.Should().Be("Example & Co");
        hits[0].Url.Should().Be("https://example.com/page");
        hits[0].Snippet.Should().Be("A snippet about the page.");
    }

    [Theory]
    [InlineData("")]
    [InlineData("<html><body>No results here</body></html>")]
    [InlineData("<html><body><a href='#'>No result class</a></body></html>")]
    public void ParseResults_NoResults_ReturnsEmpty(string html)
    {
        DuckDuckGoSearchProvider.ParseResults(html, maxResults: 8).Should().BeEmpty();
    }

    /// <summary>结果上限必须生效，否则解析整页会撑爆工具输出预算。</summary>
    [Fact]
    public void ParseResults_RespectsMaxResults()
    {
        const string html = """
            <html><body>
            <a class="result__a" href="https://example.com/1">One</a>
            <a class="result__a" href="https://example.com/2">Two</a>
            <a class="result__a" href="https://example.com/3">Three</a>
            </body></html>
            """;

        DuckDuckGoSearchProvider.ParseResults(html, maxResults: 2).Should().HaveCount(2);
    }

    /// <summary>
    /// 摘要必须关联到自己的结果：遇到下一个结果链接就停止查找，
    /// 否则第一条结果的摘要会被错配到后续链接上。
    /// </summary>
    [Fact]
    public void ParseResults_SnippetDoesNotLeakToNextResult()
    {
        const string html = """
            <html><body>
            <a class="result__a" href="https://example.com/1">First</a>
            <a class="result__a" href="https://example.com/2">Second</a>
            <a class="result__snippet">Second's snippet</a>
            </body></html>
            """;

        var hits = DuckDuckGoSearchProvider.ParseResults(html, maxResults: 8);

        hits.Should().HaveCount(2);
        hits[0].Snippet.Should().BeNull("the first result has no snippet of its own");
        hits[1].Snippet.Should().Be("Second's snippet");
    }

    [Theory]
    [InlineData("https://duckduckgo.com/l/?uddg=https%3A%2F%2Fexample.com%2Fpage", "https://example.com/page")]
    [InlineData("https://github.com/repo", "https://github.com/repo")]
    [InlineData("https://example.com/page", "https://example.com/page")]
    [InlineData("https://duckduckgo.com/?q=test", "https://duckduckgo.com/?q=test")]
    [InlineData("not a url at all", "not a url at all")]
    public void ResolveRedirect_ReturnsExpectedTarget(string href, string expected)
    {
        DuckDuckGoSearchProvider.ResolveRedirect(href).Should().Be(expected);
    }

    [Fact]
    public void NormalizeWhitespace_CollapsesRunsAndTrims()
    {
        DuckDuckGoSearchProvider.NormalizeWhitespace("  a \n\t b   c  ").Should().Be("a b c");
    }

    /// <summary>与 Tavily 不同，DDG 不需要凭据，因此始终在链路中可用。</summary>
    [Fact]
    public void IsConfigured_IsAlwaysTrue()
    {
        var provider = new DuckDuckGoSearchProvider(Substitute.For<IHttpClientFactory>());

        provider.Name.Should().Be("duckduckgo");
        provider.IsConfigured.Should().BeTrue("no API key is required for the HTML endpoint");
    }
}
