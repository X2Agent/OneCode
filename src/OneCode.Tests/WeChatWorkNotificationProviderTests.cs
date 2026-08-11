using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Web;
using OneCode.App.Services.Hooks.Notifications;
using OneCode.Core.Hooks.Notifications;

namespace OneCode.Tests;

public class WeChatWorkNotificationProviderTests
{
    [Fact]
    public async Task SendAsync_WithoutSecret_ShouldPostPlainTextMessage()
    {
        // Arrange
        var handler = new TestingHttpHandler(HttpStatusCode.OK, """{"errcode":0,"errmsg":"ok"}""");
        var provider = CreateProvider(handler);
        var message = new NotificationMessage { Text = "hello wechat work" };

        // Act
        var result = await provider.SendAsync(
            message, "https://qyapi.weixin.qq.com/cgi-bin/webhook/send?key=xxx", null, default);

        // Assert
        result.Success.Should().BeTrue();

        using var body = JsonDocument.Parse(handler.LastRequestBody!);
        body.RootElement.GetProperty("msgtype").GetString().Should().Be("text");
        body.RootElement.GetProperty("text").GetProperty("content").GetString().Should().Be("hello wechat work");

        handler.LastRequest!.RequestUri!.Query.Should().NotContain("sign");
    }

    [Fact]
    public async Task SendAsync_WithSecret_ShouldAppendSignToUrl()
    {
        // Arrange
        var handler = new TestingHttpHandler(HttpStatusCode.OK, """{"errcode":0,"errmsg":"ok"}""");
        var provider = CreateProvider(handler);

        // Act
        var result = await provider.SendAsync(
            new NotificationMessage { Text = "signed" },
            "https://qyapi.weixin.qq.com/cgi-bin/webhook/send?key=xxx",
            "my-secret",
            default);

        // Assert
        result.Success.Should().BeTrue();
        var query = handler.LastRequest!.RequestUri!.Query;
        query.Should().Contain("timestamp=");
        query.Should().Contain("sign=");
    }

    [Fact]
    public async Task SendAsync_WithSecret_SignMatchesHmacSha256Algorithm()
    {
        // Arrange: 企业微信签名算法与飞书不同！
        //   飞书:     HMAC-SHA256(key = timestamp + "\n" + secret, message = "")
        //   企业微信: HMAC-SHA256(key = secret, message = timestamp + "\n" + secret)
        var handler = new TestingHttpHandler(HttpStatusCode.OK, """{"errcode":0,"errmsg":"ok"}""");
        var provider = CreateProvider(handler);
        var secret = "test-secret-key";

        // Act
        await provider.SendAsync(
            new NotificationMessage { Text = "verify sign" },
            "https://qyapi.weixin.qq.com/cgi-bin/webhook/send?key=xxx",
            secret,
            default);

        // Assert
        var query = HttpUtility.ParseQueryString(handler.LastRequest!.RequestUri!.Query);
        var timestamp = query["timestamp"]!;
        var sign = query["sign"]!;

        // 企业微信: key=secret, message=timestamp+"\n"+secret
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var messageBytes = Encoding.UTF8.GetBytes(timestamp + "\n" + secret);
        var expectedSign = Convert.ToBase64String(hmac.ComputeHash(messageBytes));

        sign.Should().Be(expectedSign,
            "企业微信签名算法: HMAC-SHA256(key=secret, message=timestamp+\"\\n\"+secret), Base64 编码");
    }

    [Fact]
    public async Task SendAsync_WhenWeChatWorkReturnsError_ShouldReturnFail()
    {
        // Arrange
        var handler = new TestingHttpHandler(HttpStatusCode.OK, """{"errcode":93000,"errmsg":"invalid sign"}""");
        var provider = CreateProvider(handler);

        // Act
        var result = await provider.SendAsync(
            new NotificationMessage { Text = "test" },
            "https://qyapi.weixin.qq.com/cgi-bin/webhook/send?key=xxx", null, default);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("93000");
    }

    [Fact]
    public async Task SendAsync_WhenHttpFails_ShouldReturnFail()
    {
        // Arrange
        var handler = new TestingHttpHandler(HttpStatusCode.ServiceUnavailable, "unavailable");
        var provider = CreateProvider(handler);

        // Act
        var result = await provider.SendAsync(
            new NotificationMessage { Text = "test" },
            "https://qyapi.weixin.qq.com/cgi-bin/webhook/send?key=xxx", null, default);

        // Assert
        result.Success.Should().BeFalse();
        result.StatusCode.Should().Be(503);
    }

    [Fact]
    public async Task SendAsync_NonJsonResponse_TreatedAsFailure()
    {
        // Arrange: HTTP 200 但 body 非 JSON 不得视为通知成功
        var handler = new TestingHttpHandler(HttpStatusCode.OK, "ok");
        var provider = CreateProvider(handler);

        // Act
        var result = await provider.SendAsync(
            new NotificationMessage { Text = "test" },
            "https://qyapi.weixin.qq.com/cgi-bin/webhook/send?key=xxx", null, default);

        // Assert
        result.Success.Should().BeFalse("非 JSON 响应应判失败");
    }

    [Fact]
    public async Task SendAsync_WithEmptySecret_TreatedAsNoSecret()
    {
        // Arrange: 空 secret 字符串应与 null 行为一致——不附加签名
        var handler = new TestingHttpHandler(HttpStatusCode.OK, """{"errcode":0,"errmsg":"ok"}""");
        var provider = CreateProvider(handler);

        // Act
        await provider.SendAsync(
            new NotificationMessage { Text = "test" },
            "https://qyapi.weixin.qq.com/cgi-bin/webhook/send?key=xxx",
            "",
            default);

        // Assert
        handler.LastRequest!.RequestUri!.Query.Should().NotContain("sign",
            "空 secret 应与 null 行为一致，不附加签名参数");
    }

    private static WeChatWorkNotificationProvider CreateProvider(TestingHttpHandler? handler = null)
    {
        handler ??= new TestingHttpHandler(HttpStatusCode.OK, """{"errcode":0,"errmsg":"ok"}""");
        var httpClient = new HttpClient(handler);
        return new WeChatWorkNotificationProvider(httpClient);
    }
}
