using System.Net;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using OneCode.App.Tools;
using OneCode.Infrastructure.Config;

namespace OneCode.Tests;

/// <summary>
/// 验证 WebFetch 的 URL 规范化：http→https 升级必须同步剥离协议默认端口。
/// 旧实现在 UriBuilder 改 Scheme 后保留 80 端口，产出 https://host:80/... ——
/// 对明文 HTTP 端口发起 TLS 握手必然失败（真实日志：cryptorank.io 抓取 SSL error）。
/// </summary>
public sealed class WebFetchToolUrlUpgradeTests
{
    [Theory]
    [InlineData("http://cryptorank.io/news/tag/justin-sun", "https://cryptorank.io/news/tag/justin-sun")]
    [InlineData("https://example.com/a?b=1", "https://example.com/a?b=1")]
    public void NormalizeFetchUrl_HttpUpgrade_StripsDefaultPort(string input, string expected)
    {
        WebFetchTool.NormalizeFetchUrl(input).Should().Be(expected,
            "upgrading scheme must not pin the http default port 80 onto the https URL");
    }

    [Theory]
    [InlineData("http://example.com:8080/x", "https://example.com:8080/x")]
    public void NormalizeFetchUrl_ExplicitNonDefaultPort_Preserved(string input, string expected)
    {
        WebFetchTool.NormalizeFetchUrl(input).Should().Be(expected,
            "an explicit port is a deliberate choice and must survive the scheme upgrade");
    }

    [Theory]
    [InlineData("https://example.com:80/x", "https://example.com/x")]
    public void NormalizeFetchUrl_HttpsOnPort80_StripsMismatchedPort(string input, string expected)
    {
        // 模型直接传入 https://host:80/...（协议/端口错配）不走升级分支，同样必须规范化。
        WebFetchTool.NormalizeFetchUrl(input).Should().Be(expected);
    }

    [Fact]
    public async Task FetchAsync_HttpUrl_RequestLeavesAsHttpsWithoutPort80()
    {
        var ct = TestContext.Current.CancellationToken;
        Uri? seenUri = null;
        var handler = new CapturingHandler(u => seenUri = u, "plain text body");
        var httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Constants.HttpClientNames.WebFetch).Returns(httpClient);

        var sut = new WebFetchTool(
            factory,
            new MemoryCache(new MemoryCacheOptions()),
            NullLogger<WebFetchTool>.Instance);

        await sut.FetchAsync("http://example.com/justin-sun", "extract title", ct);

        seenUri.Should().NotBeNull();
        seenUri!.Scheme.Should().Be(Uri.UriSchemeHttps, "http must be upgraded before the request leaves");
        seenUri.ToString().Should().NotContain(":80", "TLS against the plaintext port always fails");
    }

    private sealed class CapturingHandler(Action<Uri> capture, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            capture(request.RequestUri!);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "text/plain"),
            });
        }
    }
}
