using Microsoft.Extensions.Caching.Memory;
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
/// the failover-chain assembly (order by <c>webSearchProvider</c>, provider without a key skipped),
/// failover error aggregation, the empty-results-as-challenge policy, query caching,
/// and the private static helpers that implement domain filtering and domain normalization.
///
/// The private helpers carry the tool's real business logic (domain matching) and are tested
/// via reflection, mirroring the pattern in
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

    // SearchAsync: query-length validation

    [Theory]
    [InlineData("")]
    [InlineData("a")]
    public async Task SearchAsync_QueryShorterThanTwoChars_ReturnsErrorJson(string query)
    {
        var ct = TestContext.Current.CancellationToken;
        var config = new ConfigManager(_tempDir);
        var sut = new WebSearchTool(config, NullLogger<WebSearchTool>.Instance, [], CreateCache());

        var result = await sut.SearchAsync(query, ct: ct);

        result.IsError.Should().BeTrue();
        result.Content.Should().Contain("query must be at least 2 characters");
    }

    // BuildChain: 故障转移链组装（顺序由 webSearchProvider 设置决定，未配置 Key 的 Tavily 跳过）

    private WebSearchTool CreateTool(IWebSearchProvider? tavily = null)
    {
        // DuckDuckGo is always available (no key required), so the chain always includes it.
        var providers = new List<IWebSearchProvider> { CreateDuckDuckGoProvider() };
        if (tavily is not null)
            providers.Add(tavily);

        return new WebSearchTool(
            new ConfigManager(_tempDir),
            NullLogger<WebSearchTool>.Instance,
            providers,
            CreateCache());
    }

    private static IWebSearchProvider CreateDuckDuckGoProvider()
    {
        var provider = Substitute.For<IWebSearchProvider>();
        provider.Name.Returns("duckduckgo");
        provider.IsConfigured.Returns(true);
        return provider;
    }

    /// <summary>每个测试独立缓存，避免用例间通过静态状态串扰。</summary>
    private static IMemoryCache CreateCache() => new MemoryCache(new MemoryCacheOptions());

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
        // DuckDuckGo 替身也会失败（未配置任何 SearchAsync 返回），因此错误里同时出现两个提供方，
        // 证明链路走完了全部已配置提供方，而不是首个失败就报错。
        var sut = new WebSearchTool(
            config, NullLogger<WebSearchTool>.Instance, [CreateDuckDuckGoProvider(), tavily], CreateCache());

        var result = await sut.SearchAsync("test query", ct: ct);

        result.IsError.Should().BeTrue();
        // 失败按稳定分类报告，不回传提供方原始异常文本（可能回显 query 与凭据）。
        result.Content.Should().Contain("tavily: no usable results");
        result.Content.Should().NotContain("quota");
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
        result.Content.Should().Contain("no usable results");
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

    // S1: 缓存必须保存原始结果，域条件每次独立应用

    /// <summary>
    /// 反证：缓存里存的必须是**未过滤**结果。旧实现把已过滤结果当缓存值，
    /// 先窄白名单再放宽时会永远拿不到先前被滤掉的候选——这条用例在缓存键/缓存值
    /// 重新带上域条件时失败。
    /// </summary>
    [Fact]
    public async Task SearchAsync_NarrowWhitelistThenWideWhitelist_SecondCallSeesAllCandidates()
    {
        var ct = TestContext.Current.CancellationToken;
        var tavily = CreateTavilyProvider(configured: true);
        tavily.SearchAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([
                new WebSearchResult("A", "https://a.example.com/1", "a"),
                new WebSearchResult("B", "https://b.example.com/2", "b"),
            ]);
        var sut = CreateTool(tavily);

        var narrow = await sut.SearchAsync("shared query", allowed_domains: ["a.example.com"], ct: ct);
        var wide = await sut.SearchAsync("shared query", allowed_domains: ["example.com"], ct: ct);

        narrow.Content.Should().Contain("https://a.example.com/1");
        narrow.Content.Should().NotContain("https://b.example.com/2");
        wide.Content.Should().Contain("https://b.example.com/2",
            "the cached value is the unfiltered provider output, so widening the whitelist recovers it");
        await tavily.Received(1).SearchAsync("shared query", Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    /// <summary>域条件变化不得回源；同一 query 的提供方调用只发生一次。</summary>
    [Fact]
    public async Task SearchAsync_DifferentDomainFilters_DoNotTriggerProviderRefetch()
    {
        var ct = TestContext.Current.CancellationToken;
        var tavily = CreateTavilyProvider(configured: true);
        tavily.SearchAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([new WebSearchResult("A", "https://a.example.com/1", "a")]);
        var sut = CreateTool(tavily);

        await sut.SearchAsync("shared query", blocked_domains: ["blocked.com"], ct: ct);
        await sut.SearchAsync("shared query", ct: ct);

        await tavily.Received(1).SearchAsync("shared query", Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// 调用方取消必须传播，且不得触发提供方故障转移——
    /// 否则用户停止后仍会继续向外发请求。
    /// </summary>
    [Fact]
    public async Task SearchAsync_CallerCancelled_PropagatesAndDoesNotFailOver()
    {
        var tavily = CreateTavilyProvider(configured: true);
        tavily.SearchAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<WebSearchResult>>(_ => throw new OperationCanceledException());
        var sut = CreateTool(tavily);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var act = () => sut.SearchAsync("cancel me", ct: cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
