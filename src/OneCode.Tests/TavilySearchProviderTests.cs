using System.Net;
using NSubstitute;
using OneCode.Core.Config;
using OneCode.Infrastructure.Search;

namespace OneCode.Tests;

/// <summary>
/// Tavily 搜索提供方适配测试：JSON 解析（title/url/content → Title/Url/Snippet）、
/// 请求形状（POST + Bearer + query/max_results）、错误映射（401/403/429）、
/// Key 缺失防护。HTTP 通过自定义 HttpMessageHandler 桩模拟，不触网。
/// </summary>
public sealed class TavilySearchProviderTests
{
    private const string ApiKey = "tvly-test-key";

    private sealed class StubHandler(HttpStatusCode status, string json, Action<HttpRequestMessage>? inspect = null)
        : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastRequest = request;
            inspect?.Invoke(request);
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
            });
        }
    }

    private static TavilySearchProvider CreateProvider(StubHandler handler)
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(new HttpClient(handler));
        var config = Substitute.For<IConfigManager>();
        config.Current.Returns(ConfigSnapshot.FromEffective(
            new AppSettings { TavilyApiKey = ApiKey }));
        return new TavilySearchProvider(factory, config);
    }

    [Fact]
    public async Task SearchAsync_ValidResponse_MapsResultsToWebSearchHits()
    {
        var ct = TestContext.Current.CancellationToken;
        const string json = """
            {
              "results": [
                { "title": "OneCode CLI", "url": "https://example.com/onecode", "content": "A CLI coding assistant" },
                { "title": "  ", "url": "https://example.com/blank", "content": "blank title must be dropped" },
                { "title": "No Url", "url": "  ", "content": "blank url must be dropped" }
              ]
            }
            """;
        var sut = CreateProvider(new StubHandler(HttpStatusCode.OK, json));

        var hits = await sut.SearchAsync("onecode", maxResults: 8, ct);

        hits.Should().HaveCount(1);
        hits[0].Title.Should().Be("OneCode CLI");
        hits[0].Url.Should().Be("https://example.com/onecode");
        hits[0].Snippet.Should().Be("A CLI coding assistant");
    }

    [Fact]
    public async Task SearchAsync_SendsBearerTokenAndQueryPayload()
    {
        var ct = TestContext.Current.CancellationToken;
        HttpRequestMessage? captured = null;
        string? body = null;
        var handler = new StubHandler(HttpStatusCode.OK, """{"results":[]}""",
            request =>
            {
                captured = request;
                body = request.Content is null ? null : request.Content.ReadAsStringAsync(ct).GetAwaiter().GetResult();
            });
        var sut = CreateProvider(handler);

        await sut.SearchAsync("dotnet 10 features", maxResults: 5, ct);

        captured.Should().NotBeNull();
        captured!.Method.Should().Be(HttpMethod.Post);
        captured.RequestUri!.ToString().Should().Be("https://api.tavily.com/search");
        captured.Headers.Authorization.Should().NotBeNull();
        captured.Headers.Authorization!.Scheme.Should().Be("Bearer");
        captured.Headers.Authorization.Parameter.Should().Be(ApiKey);
        body.Should().Contain("\"query\":\"dotnet 10 features\"");
        body.Should().Contain("\"max_results\":5");
    }

    [Fact]
    public async Task SearchAsync_Http401_ThrowsWithApiKeyGuidance()
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = CreateProvider(new StubHandler(HttpStatusCode.Unauthorized, """{"detail":"invalid key"}"""));

        var act = () => sut.SearchAsync("q", maxResults: 8, ct);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*401*");
    }

    [Fact]
    public async Task SearchAsync_Http429_ThrowsQuotaExceeded()
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = CreateProvider(new StubHandler(HttpStatusCode.TooManyRequests, """{"detail":"quota"}"""));

        var act = () => sut.SearchAsync("q", maxResults: 8, ct);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*429*");
    }

    [Fact]
    public void IsConfigured_WithoutApiKey_IsFalse()
    {
        var factory = Substitute.For<IHttpClientFactory>();
        var config = Substitute.For<IConfigManager>();
        config.Current.Returns(ConfigSnapshot.FromEffective(new AppSettings()));

        var sut = new TavilySearchProvider(factory, config);

        sut.IsConfigured.Should().BeFalse();
    }

    [Fact]
    public async Task SearchAsync_WithoutApiKey_ThrowsWithConfigurationGuidance()
    {
        var ct = TestContext.Current.CancellationToken;
        var factory = Substitute.For<IHttpClientFactory>();
        var config = Substitute.For<IConfigManager>();
        config.Current.Returns(ConfigSnapshot.FromEffective(new AppSettings()));
        var sut = new TavilySearchProvider(factory, config);

        var act = () => sut.SearchAsync("q", maxResults: 8, ct);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*TAVILY_API_KEY*");
    }
}
