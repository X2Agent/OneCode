using System.Reflection;
using NSubstitute;
using OneCode.App.Tools;
using OneCode.Infrastructure.Config;

namespace OneCode.Tests;

/// <summary>
/// Unit tests for <see cref="WebSearchTool"/> — covers query-length validation,
/// the Brave-provider-without-API-key guard, and the private static helpers that
/// implement DuckDuckGo HTML parsing, domain filtering, domain normalization,
/// redirect resolution, and HTML-text cleaning.
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
        var sut = new WebSearchTool(config, httpClientFactory);

        var result = await sut.SearchAsync(query, ct: ct);

        result.IsError.Should().BeTrue();
        result.Content.Should().Contain("query must be at least 2 characters");
    }

    // SearchAsync: Brave provider without API key

    [Fact]
    public async Task SearchAsync_BraveProviderWithoutApiKey_ReturnsErrorJson()
    {
        var ct = TestContext.Current.CancellationToken;
        const string providerKey = "ONECODE_WEB_SEARCH_PROVIDER";
        const string braveKey = "BRAVE_SEARCH_API_KEY";
        const string oneCodeKey = "ONECODE_WEB_SEARCH_API_KEY";
        var originalProvider = Environment.GetEnvironmentVariable(providerKey);
        var originalBrave = Environment.GetEnvironmentVariable(braveKey);
        var originalOneCode = Environment.GetEnvironmentVariable(oneCodeKey);
        try
        {
            Environment.SetEnvironmentVariable(providerKey, "brave");
            Environment.SetEnvironmentVariable(braveKey, null);
            Environment.SetEnvironmentVariable(oneCodeKey, null);

            var config = new ConfigManager(_tempDir);
            var httpClientFactory = Substitute.For<IHttpClientFactory>();
            var sut = new WebSearchTool(config, httpClientFactory);

            var result = await sut.SearchAsync("test query", ct: ct);

            result.IsError.Should().BeTrue();
            result.Content.Should().Contain("Brave search requires BRAVE_SEARCH_API_KEY");
        }
        finally
        {
            Environment.SetEnvironmentVariable(providerKey, originalProvider);
            Environment.SetEnvironmentVariable(braveKey, originalBrave);
            Environment.SetEnvironmentVariable(oneCodeKey, originalOneCode);
        }
    }

    // ResolveProvider consumes only the effective snapshot. Environment precedence is
    // resolved once by ConfigManager and covered by ConfigManagerTests.

    [Fact]
    public void ResolveProvider_UsesEffectiveSnapshotWithoutReadingEnvironment()
    {
        const string providerKey = "ONECODE_WEB_SEARCH_PROVIDER";
        var original = Environment.GetEnvironmentVariable(providerKey);
        try
        {
            Environment.SetEnvironmentVariable(providerKey, "brave");
            var settings = new AppSettings { WebSearchProvider = "duckduckgo" };

            var result = InvokeResolveProvider(settings);

            // WebSearchProvider is a private enum: Brave=0, DuckDuckGo=1
            ((int)result).Should().Be(1, "provider consumers must not re-resolve environment variables");
        }
        finally
        {
            Environment.SetEnvironmentVariable(providerKey, original);
        }
    }

    [Fact]
    public void ResolveProvider_EffectiveSnapshotValue_IsUsed()
    {
        const string providerKey = "ONECODE_WEB_SEARCH_PROVIDER";
        var original = Environment.GetEnvironmentVariable(providerKey);
        try
        {
            Environment.SetEnvironmentVariable(providerKey, null);
            var settings = new AppSettings { WebSearchProvider = "brave" };

            var result = InvokeResolveProvider(settings);

            ((int)result).Should().Be(0, "settings 'brave' should resolve to Brave when env var is absent");
        }
        finally
        {
            Environment.SetEnvironmentVariable(providerKey, original);
        }
    }

    [Fact]
    public void ResolveProvider_InvalidSnapshotValue_DefaultsToDuckDuckGo()
    {
        var settings = new AppSettings { WebSearchProvider = "not-a-real-provider" };

        var result = InvokeResolveProvider(settings);

        ((int)result).Should().Be(1, "invalid provider value must fall back to DuckDuckGo");
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
