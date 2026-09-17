using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using OneCode.App.Tools;
using OneCode.Infrastructure.Config;

namespace OneCode.Tests;

/// <summary>
/// 验证 WebFetch 不做浏览器渲染硬编码降级：JS-only shell 页面仅返回降级提示文本，
/// 是否改用 Playwright MCP（browser_navigate / browser_snapshot）由模型自主决策。
/// </summary>
public sealed class WebFetchToolJsHintTests
{
    private const string SpaHtml =
        "<html><body><noscript>You need to enable JavaScript to run this app.</noscript>" +
        "<div id=\"root\"></div></body></html>";

    [Fact]
    public async Task FetchAsync_JsShell_ReturnsHintInsteadOfRendering()
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = CreateSut(SpaHtml);

        var result = await sut.FetchAsync("https://example.com/", "extract title", ct);

        result.IsError.Should().BeFalse();
        result.Content.Should().Contain("enable JavaScript");
        result.Content.Should().Contain("BrowserFetch");
        result.Content.Should().NotContain("McpConnect");
    }

    private static WebFetchTool CreateSut(string htmlBody)
    {
        var handler = new FixedHtmlHandler(htmlBody);
        var httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Constants.HttpClientNames.WebFetch).Returns(httpClient);

        return new WebFetchTool(factory, new MemoryCache(new MemoryCacheOptions()), NullLogger<WebFetchTool>.Instance);
    }

    private sealed class FixedHtmlHandler(string html) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(html),
            };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("text/html");
            return Task.FromResult(response);
        }
    }
}