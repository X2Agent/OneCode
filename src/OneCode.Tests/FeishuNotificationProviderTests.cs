using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Web;
using OneCode.App.Services.Hooks.Notifications;
using OneCode.Core.Hooks.Notifications;

namespace OneCode.Tests;

public class FeishuNotificationProviderTests
{
    [Fact]
    public async Task SendAsync_WithoutSecret_ShouldPostPlainTextMessage()
    {
        // Arrange
        var handler = new TestingHttpHandler(HttpStatusCode.OK, """{"code":0,"msg":"ok"}""");
        var provider = CreateProvider(handler);
        var message = new NotificationMessage { Text = "hello feishu" };

        // Act
        var result = await provider.SendAsync(message, "https://open.feishu.cn/open-apis/bot/v2/hook/xxx", null, default);

        // Assert
        result.Success.Should().BeTrue();
        handler.LastRequest.Should().NotBeNull();

        using var body = JsonDocument.Parse(handler.LastRequestBody!);
        body.RootElement.GetProperty("msg_type").GetString().Should().Be("text");
        body.RootElement.GetProperty("content").GetProperty("text").GetString().Should().Be("hello feishu");

        // 无 secret 时不应附加 sign 查询参数
        handler.LastRequest!.RequestUri!.Query.Should().NotContain("sign");
    }

    [Fact]
    public async Task SendAsync_WithSecret_ShouldAppendSignToUrl()
    {
        // Arrange
        var handler = new TestingHttpHandler(HttpStatusCode.OK, """{"code":0,"msg":"ok"}""");
        var provider = CreateProvider(handler);
        var message = new NotificationMessage { Text = "signed message" };

        // Act
        var result = await provider.SendAsync(
            message, "https://open.feishu.cn/open-apis/bot/v2/hook/xxx", "my-secret", default);

        // Assert
        result.Success.Should().BeTrue();
        var query = handler.LastRequest!.RequestUri!.Query;
        query.Should().Contain("timestamp=");
        query.Should().Contain("sign=");
    }

    [Fact]
    public async Task SendAsync_WithSecret_SignMatchesHmacSha256Algorithm()
    {
        // Arrange
        var handler = new TestingHttpHandler(HttpStatusCode.OK, """{"code":0,"msg":"ok"}""");
        var provider = CreateProvider(handler);
        var secret = "test-secret-key";

        // Act
        await provider.SendAsync(
            new NotificationMessage { Text = "verify sign" },
            "https://open.feishu.cn/open-apis/bot/v2/hook/xxx",
            secret,
            default);

        // Assert: 验证签名算法 HMAC-SHA256(key = timestamp + "\n" + secret, message = "")
        var query = HttpUtility.ParseQueryString(handler.LastRequest!.RequestUri!.Query);
        var timestamp = query["timestamp"]!;
        var sign = query["sign"]!;

        var expectedKey = timestamp + "\n" + secret;
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(expectedKey));
        var expectedSign = Convert.ToBase64String(hmac.ComputeHash(Array.Empty<byte>()));

        sign.Should().Be(expectedSign, "飞书签名算法: HMAC-SHA256(key=timestamp+\"\\n\"+secret, message=\"\"), Base64 编码");
    }

    [Fact]
    public async Task SendAsync_WhenFeishuReturnsError_ShouldReturnFail()
    {
        // Arrange
        var handler = new TestingHttpHandler(HttpStatusCode.OK, """{"code":19021,"msg":"sign match fail"}""");
        var provider = CreateProvider(handler);

        // Act
        var result = await provider.SendAsync(
            new NotificationMessage { Text = "test" }, "https://open.feishu.cn/open-apis/bot/v2/hook/xxx", null, default);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("19021");
    }

    [Fact]
    public async Task SendAsync_WhenHttpFails_ShouldReturnFail()
    {
        // Arrange
        var handler = new TestingHttpHandler(HttpStatusCode.InternalServerError, "server error");
        var provider = CreateProvider(handler);

        // Act
        var result = await provider.SendAsync(
            new NotificationMessage { Text = "test" }, "https://open.feishu.cn/open-apis/bot/v2/hook/xxx", null, default);

        // Assert
        result.Success.Should().BeFalse();
        result.StatusCode.Should().Be(500);
    }

    [Fact]
    public async Task SendAsync_NonJsonResponse_TreatedAsFailure()
    {
        // Arrange: HTTP 200 但 body 非 JSON（如 HTML/纯文本）不得视为通知成功
        var handler = new TestingHttpHandler(HttpStatusCode.OK, "ok");
        var provider = CreateProvider(handler);

        // Act
        var result = await provider.SendAsync(
            new NotificationMessage { Text = "test" }, "https://open.feishu.cn/open-apis/bot/v2/hook/xxx", null, default);

        // Assert
        result.Success.Should().BeFalse("非 JSON 响应应判失败，避免 502 HTML 等被当成成功");
    }

    [Fact]
    public async Task SendAsync_WithEmptySecret_TreatedAsNoSecret()
    {
        // Arrange: 空 secret 字符串应与 null 行为一致——不附加签名
        var handler = new TestingHttpHandler(HttpStatusCode.OK, """{"code":0,"msg":"ok"}""");
        var provider = CreateProvider(handler);

        // Act
        await provider.SendAsync(
            new NotificationMessage { Text = "test" },
            "https://open.feishu.cn/open-apis/bot/v2/hook/xxx",
            "",
            default);

        // Assert
        handler.LastRequest!.RequestUri!.Query.Should().NotContain("sign",
            "空 secret 应与 null 行为一致，不附加签名参数");
    }

    private static FeishuNotificationProvider CreateProvider(TestingHttpHandler? handler = null)
    {
        handler ??= new TestingHttpHandler(HttpStatusCode.OK, """{"code":0,"msg":"ok"}""");
        var httpClient = new HttpClient(handler);
        return new FeishuNotificationProvider(httpClient);
    }
}

/// <summary>
/// 测试用 HttpMessageHandler——记录最后一次请求并返回预设响应。
/// 避免直接 mock HttpMessageHandler 的 protected SendAsync 方法。
/// </summary>
internal sealed class TestingHttpHandler : HttpMessageHandler
{
    private readonly HttpStatusCode _statusCode;
    private readonly string _responseContent;

    public HttpRequestMessage? LastRequest { get; private set; }
    public string? LastRequestBody { get; private set; }

    public TestingHttpHandler(HttpStatusCode statusCode, string responseContent)
    {
        _statusCode = statusCode;
        _responseContent = responseContent;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        LastRequest = request;
        LastRequestBody = request.Content is not null
            ? await request.Content.ReadAsStringAsync(cancellationToken)
            : null;
        var response = new HttpResponseMessage(_statusCode)
        {
            Content = new StringContent(_responseContent),
        };
        return response;
    }
}
