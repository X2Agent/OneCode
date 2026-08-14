using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using OneCode.App.Tools;

namespace OneCode.Tests;

public sealed class WebFetchToolBrowserFallbackTests
{
    private const string SpaHtml =
        "<html><body><noscript>You need to enable JavaScript to run this app.</noscript>" +
        "<div id=\"root\"></div></body></html>";

    [Fact]
    public async Task FetchAsync_JsHint_UsesBrowserRendererWhenAvailable()
    {
        var ct = TestContext.Current.CancellationToken;
        var renderer = Substitute.For<IBrowserPageRenderer>();
        renderer.RenderAsync("https://example.com/", Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns("Rendered SPA heading\nMain content from accessibility tree");

        var sut = CreateSut(SpaHtml, renderer);

        var result = await sut.FetchAsync("https://example.com/", "extract title", ct);

        result.IsError.Should().BeFalse();
        result.Content.Should().Contain("Rendered SPA heading");
        result.Content.Should().Contain("accessibility tree");
        await renderer.Received(1).RenderAsync(
            "https://example.com/",
            Arg.Any<int>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task FetchAsync_JsHint_KeepsHttpMarkdownWhenRendererReturnsNull()
    {
        var ct = TestContext.Current.CancellationToken;
        var renderer = Substitute.For<IBrowserPageRenderer>();
        renderer.RenderAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns((string?)null);

        var sut = CreateSut(SpaHtml, renderer);

        var result = await sut.FetchAsync("https://example.com/", "extract body", ct);

        result.IsError.Should().BeFalse();
        result.Content.Should().Contain("enable JavaScript");
        result.Content.Should().NotContain("Rendered SPA");
    }

    [Fact]
    public async Task FetchAsync_JsHint_KeepsHttpMarkdownWhenRendererMissing()
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = CreateSut(SpaHtml, browserRenderer: null);

        var result = await sut.FetchAsync("https://example.com/", "extract body", ct);

        result.IsError.Should().BeFalse();
        result.Content.Should().Contain("enable JavaScript");
    }

    private static WebFetchTool CreateSut(string htmlBody, IBrowserPageRenderer? browserRenderer)
    {
        var handler = new FixedHtmlHandler(htmlBody);
        var httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient("WebFetch").Returns(httpClient);

        return new WebFetchTool(
            factory,
            new WebFetchCache(),
            NullLogger<WebFetchTool>.Instance,
            browserRenderer);
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
