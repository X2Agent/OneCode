using Microsoft.Extensions.Logging.Abstractions;

using OneCode.Core.Config;
using OneCode.Core.Search;
using System.Reflection;
using NSubstitute;
using OneCode.App.Tools;
using OneCode.Infrastructure.Config;

namespace OneCode.Tests;

/// <summary>
/// Unit tests for <see cref="WebSearchTool"/> — covers query-length validation,
/// the failover-chain assembly (order by <c>webSearchProvider</c>, keyless Tavily skipped),
/// failover error aggregation, the empty-results-as-challenge policy, query caching,
/// and the private static helpers that implement DuckDuckGo HTML parsing, domain
/// filtering, domain normalization, redirect resolution, and HTML-text cleaning.
///
/// The private helpers carry the tool's real business logic (HTML parsing,
/// domain matching) and are tested via reflection, mirroring the pattern in
/// <see cref="WebFetchToolSsrfTests"/>.
/// </summary>
public sealed class WebSearchToolTests : IDisposable
{
    private readonly string _tempDir;

    public WebSearchToolTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"WebSearchToolTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    // Reflection helpers for private static methods

    private static IReadOnlyList<(string Title, string Url, string? Snippet)> InvokeParseDuckDuckGoResults(string html)
    {
        var method = typeof(WebSearchTool).GetMethod(
            "ParseDuckDuckGoResults",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        var result = (System.Collections.IEnumerable)method.Invoke(null, new object[] { html })!;
        return ExtractHits(result);
    }

    private static IReadOnlyList<(string Title, string Url, string? Snippet)> InvokeFilterDomains(
        IEnumerable<(string Title, string Url, string? Snippet)> hits,
        string[]? allowedDomains,
        string[]? blockedDomains)
    {
        var searchHitType = typeof(WebSearchTool).GetNestedType("SearchHit", BindingFlags.NonPublic | BindingFlags.Instance)!
            ?? typeof(WebSearchTool).GetNestedType("SearchHit", BindingFlags.NonPublic | BindingFlags.Public)!;
        var hitObjects = hits.Select(h => CreateSearchHit(searchHitType, h.Title, h.Url, h.Snippet)).ToArray();
        var hitListType = typeof(List<>).MakeGenericType(searchHitType);
        var hitList = (System.Collections.IList)Activator.CreateInstance(hitListType)!;
        foreach (var obj in hitObjects)
            hitList.Add(obj);

        var method = typeof(WebSearchTool).GetMethod(
            "FilterDomains",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        var result = (System.Collections.IEnumerable)method.Invoke(null, new object?[] { hitList, allowedDomains, blockedDomains })!;
        return ExtractHits(result);
    }

    private static object CreateSearchHit(Type searchHitType, string title, string url, string? snippet)
    {
        var constructor = searchHitType.GetConstructor(new[] { typeof(string), typeof(string), typeof(string) })!;
        return constructor.Invoke(new object?[] { title, url, snippet });
    }

    private static IReadOnlyList<(string Title, string Url, string? Snippet)> ExtractHits(System.Collections.IEnumerable source)
    {
        var list = new List<(string Title, string Url, string? Snippet)>();
        foreach (var item in source)
        {
            var type = item.GetType();
            var title = (string?)type.GetProperty("Title")?.GetValue(item);
            var url = (string?)type.GetProperty("Url")?.GetValue(item);
            var snippet = (string?)type.GetProperty("Snippet")?.GetValue(item);
            list.Add((title ?? "", url ?? "", snippet));
        }
        return list;
    }

    private static string InvokeNormalizeDomain(string domain)
    {
        var method = typeof(WebSearchTool).GetMethod(
            "NormalizeDomain",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        return (string)method.Invoke(null, new object[] { domain })!;
    }

    private static string InvokeResolveDuckDuckGoRedirect(string href)
    {
        var method = typeof(WebSearchTool).GetMethod(
            "ResolveDuckDuckGoRedirect",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        return (string)method.Invoke(null, new object[] { href })!;
    }

    private static string InvokeCleanHtmlText(string text)
    {
        var method = typeof(WebSearchTool).GetMethod(
            "CleanHtmlText",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        return (string)method.Invoke(null, new object[] { text })!;
    }

    private static object InvokeResolveProvider(AppSettings settings)
    {
        var method = typeof(WebSearchTool).GetMethod(
            "ResolveProvider",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        return method.Invoke(null, new object[] { settings })!;
    }

    // SearchAsync: query-length validation

    [Theory]
    [InlineData("")]
    [InlineData("a")]
    public async Task SearchAsync_QueryShorterThanTwoChars_ReturnsErrorJson(string query)
    {
        var ct = TestContext.Current.CancellationToken;
        var config = new ConfigManager(_tempDir);
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        var sut = new WebSearchTool(config, httpClientFactory, NullLogger<WebSearchTool>.Instance, []);

        var result = await sut.SearchAsync(query, ct: ct);

        result.IsError.Should().BeTrue();
        result.Content.Should().Contain("query must be at least 2 characters");
    }

    // BuildChain: 故障转移链组装（顺序由 webSearchProvider 设置决定，未配置 Key 的 Tavily 跳过）

    private WebSearchTool CreateTool(IWebSearchProvider? tavily = null)
    {
        var providers = tavily is null ? [] : new[] { tavily };
        return new WebSearchTool(
            new ConfigManager(_tempDir),
            Substitute.For<IHttpClientFactory>(),
            NullLogger<WebSearchTool>.Instance,
            providers);
    }

    private static IWebSearchProvider CreateTavilyProvider(bool configured)
    {
        var provider = Substitute.For<IWebSearchProvider>();
        provider.Name.Returns("tavily");
        provider.IsConfigured.Returns(configured);
        return provider;
    }

    private static IReadOnlyList<string> InvokeChainNames(WebSearchTool tool, AppSettings settings)
    {
        var method = typeof(WebSearchTool).GetMethod(
            "BuildChain", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var chain = (System.Collections.IEnumerable)method.Invoke(tool, new object?[] { settings, "test query" })!;

        var names = new List<string>();
        foreach (var entry in chain)
            names.Add((string)entry.GetType().GetField("Item1")!.GetValue(entry)!);
        return names;
    }

    [Fact]
    public void BuildChain_TavilyPrimaryWithConfiguredKey_TavilyFirstThenDuckDuckGo()
    {
        var sut = CreateTool(CreateTavilyProvider(configured: true));
        var settings = new AppSettings { WebSearchProvider = "tavily" };

        InvokeChainNames(sut, settings).Should().Equal("tavily", "duckduckgo");
    }

    [Fact]
    public void BuildChain_TavilyPrimaryWithoutKey_SkipsTavilyStraightToDuckDuckGo()
    {
        var sut = CreateTool(CreateTavilyProvider(configured: false));
        var settings = new AppSettings { WebSearchProvider = "tavily" };

        InvokeChainNames(sut, settings).Should().Equal("duckduckgo");
    }

    [Fact]
    public void BuildChain_DefaultWithConfiguredKey_DuckDuckGoFirstThenTavily()
    {
        var sut = CreateTool(CreateTavilyProvider(configured: true));
        var settings = new AppSettings { WebSearchProvider = "duckduckgo" };

        InvokeChainNames(sut, settings).Should().Equal("duckduckgo", "tavily");
    }

    [Fact]
    public void BuildChain_DefaultWithoutKey_OnlyDuckDuckGo()
    {
        var sut = CreateTool(CreateTavilyProvider(configured: false));
        var settings = new AppSettings { WebSearchProvider = "duckduckgo" };

        InvokeChainNames(sut, settings).Should().Equal("duckduckgo");
    }

    [Fact]
    public void BuildChain_InvalidSettingValue_TreatedAsDuckDuckGoPrimary()
    {
        var sut = CreateTool(CreateTavilyProvider(configured: false));
        var settings = new AppSettings { WebSearchProvider = "not-a-real-provider" };

        InvokeChainNames(sut, settings).Should().Equal("duckduckgo");
    }

    // SearchAsync: 故障转移与错误聚合

    [Fact]
    public async Task SearchAsync_TavilyFails_FallsBackToDuckDuckGoAndAggregatesErrors()
    {
        var ct = TestContext.Current.CancellationToken;
        var config = new ConfigManager(_tempDir);
        await config.ApplyAsync(ConfigPatch.Set(ConfigScope.User, "webSearchProvider", "tavily"), ct);

        var tavily = CreateTavilyProvider(configured: true);
        tavily.SearchAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException<IReadOnlyList<WebSearchResult>>(new InvalidOperationException("quota")));
        // IHttpClientFactory 替身 CreateClient 返回 null → DuckDuckGo 尝试同样失败，
        // 错误中出现 "duckduckgo:" 即证明回退真实发生而非 Tavily 失败后直接报错。
        var sut = new WebSearchTool(
            config, Substitute.For<IHttpClientFactory>(), NullLogger<WebSearchTool>.Instance, [tavily]);

        var result = await sut.SearchAsync("test query", ct: ct);

        result.IsError.Should().BeTrue();
        result.Content.Should().Contain("tavily: quota");
        result.Content.Should().Contain("duckduckgo:");
    }

    [Fact]
    public async Task SearchAsync_ProviderReturnsEmptyResults_TreatedAsFailureAndFallsBack()
    {
        var ct = TestContext.Current.CancellationToken;
        var tavily = CreateTavilyProvider(configured: true);
        tavily.SearchAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([]);
        var sut = CreateTool(tavily);

        var result = await sut.SearchAsync("test query", ct: ct);

        result.IsError.Should().BeTrue();
        result.Content.Should().Contain("returned no results");
    }

    [Fact]
    public async Task SearchAsync_TavilyReturnsHits_UsesTavilyWithoutFallback()
    {
        var ct = TestContext.Current.CancellationToken;
        var tavily = CreateTavilyProvider(configured: true);
        tavily.SearchAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([new WebSearchResult("OneCode", "https://example.com/cli", "cli")]);
        var sut = CreateTool(tavily);

        var result = await sut.SearchAsync("onecode cli", ct: ct);

        result.IsError.Should().BeFalse();
        result.Content.Should().Contain("tavily");
        result.Content.Should().Contain("https://example.com/cli");
    }

    [Fact]
    public async Task SearchAsync_SameQueryTwice_SecondCallServedFromCacheWithoutProviderHit()
    {
        var ct = TestContext.Current.CancellationToken;
        var tavily = CreateTavilyProvider(configured: true);
        tavily.SearchAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([new WebSearchResult("OneCode", "https://example.com/cli", "cli")]);
        var sut = CreateTool(tavily);

        var first = await sut.SearchAsync("onecode cli", ct: ct);
        var second = await sut.SearchAsync("onecode cli", ct: ct);

        first.IsError.Should().BeFalse();
        second.IsError.Should().BeFalse();
        second.Content.Should().Contain("cached");
        // 配额保护：TTL 内重复查询只允许触发一次真实提供方调用。
        await tavily.Received(1).SearchAsync("onecode cli", Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    // ParseDuckDuckGoResults

    private const string SampleDuckDuckGoHtml = """
        <html><body>
        <a class="result__a" href="https://duckduckgo.com/l/?uddg=https%3A%2F%2Fexample.com%2Fpage&amp;rut=abc">Example Title</a>
        <a class="result__snippet" href="#">This is the <b>snippet</b> text</a>
        <a class="result__a" href="https://duckduckgo.com/l/?uddg=https%3A%2F%2Fgithub.com%2Frepo&amp;rut=def">GitHub Repo</a>
        <div class="result__snippet">A repo description</div>
        </body></html>
        """;

    [Fact]
    public void ParseDuckDuckGoResults_ValidHtml_ExtractsAllFields()
    {
        var hits = InvokeParseDuckDuckGoResults(SampleDuckDuckGoHtml);

        hits.Should().HaveCount(2);
        hits.Should().Contain(h => h.Title == "Example Title");
        hits.Should().Contain(h => h.Title == "GitHub Repo");
        hits.Should().Contain(h => h.Url == "https://example.com/page");
        hits.Should().Contain(h => h.Url == "https://github.com/repo");

        var example = hits.Single(h => h.Title == "Example Title");
        example.Snippet.Should().Contain("snippet");
        example.Snippet.Should().NotContain("<b>");
    }

    [Theory]
    [InlineData("")]
    [InlineData("<html><body>No results here</body></html>")]
    [InlineData("<html><body><a href='#'>No result class</a></body></html>")]
    public void ParseDuckDuckGoResults_NoResults_ReturnsEmpty(string html)
    {
        var hits = InvokeParseDuckDuckGoResults(html);
        hits.Should().BeEmpty();
    }

    // NormalizeDomain

    [Theory]
    [InlineData("www.example.com", "example.com")]
    [InlineData("WWW.EXAMPLE.COM", "example.com")]
    [InlineData("example.com", "example.com")]
    [InlineData(" example.com ", "example.com")]
    [InlineData(".example.com.", "example.com")]
    public void NormalizeDomain_StripsWwwTrimsAndLowercases(string input, string expected)
    {
        InvokeNormalizeDomain(input).Should().Be(expected);
    }

    // ResolveDuckDuckGoRedirect

    [Fact]
    public void ResolveDuckDuckGoRedirect_RedirectWithUddg_ExtractsActualUrl()
    {
        var href = "https://duckduckgo.com/l/?uddg=https%3A%2F%2Fexample.com%2Fpage&rut=abc";

        InvokeResolveDuckDuckGoRedirect(href).Should().Be("https://example.com/page");
    }

    [Theory]
    [InlineData("https://example.com/page")]
    [InlineData("https://github.com/repo")]
    public void ResolveDuckDuckGoRedirect_NonDuckDuckGoHost_ReturnsOriginal(string url)
    {
        InvokeResolveDuckDuckGoRedirect(url).Should().Be(url);
    }

    [Fact]
    public void ResolveDuckDuckGoRedirect_DuckDuckGoHostWithoutUddg_ReturnsOriginal()
    {
        var href = "https://duckduckgo.com/?q=test";

        InvokeResolveDuckDuckGoRedirect(href).Should().Be(href);
    }

    [Fact]
    public void ResolveDuckDuckGoRedirect_InvalidUri_ReturnsOriginal()
    {
        const string href = "not a url at all";

        InvokeResolveDuckDuckGoRedirect(href).Should().Be(href);
    }

    // CleanHtmlText

    [Fact]
    public void CleanHtmlText_StripsTagsAndCollapsesWhitespace()
    {
        InvokeCleanHtmlText("<b>Hello</b>   <i>World</i>").Should().Be("Hello World");
    }

    [Fact]
    public void CleanHtmlText_DecodesHtmlEntities()
    {
        InvokeCleanHtmlText("a &amp; b &lt; c").Should().Be("a & b < c");
    }

    [Fact]
    public void CleanHtmlText_PureText_ReturnsUnchanged()
    {
        InvokeCleanHtmlText("just plain text").Should().Be("just plain text");
    }

    // FilterDomains

    private static IEnumerable<(string Title, string Url, string? Snippet)> SampleHits() =>
    [
        ("GitHub", "https://github.com/repo", "repo"),
        ("Example", "https://example.com/page", "page"),
        ("Docs", "https://docs.example.com/guide", "guide"),
        ("Google", "https://google.com/search", "search"),
    ];

    [Fact]
    public void FilterDomains_AllowedDomains_KeepsOnlyMatchingHostsAndSubdomains()
    {
        var hits = InvokeFilterDomains(SampleHits(), allowedDomains: ["example.com"], blockedDomains: null);

        hits.Should().HaveCount(2);
        hits.Should().Contain(h => h.Url == "https://example.com/page");
        hits.Should().Contain(h => h.Url == "https://docs.example.com/guide");
    }

    [Fact]
    public void FilterDomains_BlockedDomains_RemovesMatchingHosts()
    {
        var hits = InvokeFilterDomains(SampleHits(), allowedDomains: null, blockedDomains: ["google.com"]);

        hits.Should().NotContain(h => h.Url.Contains("google.com"));
        hits.Should().HaveCount(3);
    }

    [Fact]
    public void FilterDomains_InvalidUrls_RemovedFromResults()
    {
        var input = new List<(string Title, string Url, string? Snippet)>
        {
            ("Valid", "https://example.com/page", "ok"),
            ("Invalid", "not-a-url", "bad"),
        };

        var hits = InvokeFilterDomains(input, allowedDomains: null, blockedDomains: null);

        hits.Should().HaveCount(1);
        hits.Should().OnlyContain(h => h.Url == "https://example.com/page");
    }

    [Fact]
    public void FilterDomains_EmptyAllowedAndBlocked_KeepsAllValidUrls()
    {
        var hits = InvokeFilterDomains(SampleHits(), allowedDomains: null, blockedDomains: null);

        hits.Should().HaveCount(4);
    }
}
