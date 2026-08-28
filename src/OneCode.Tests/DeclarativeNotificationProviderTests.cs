using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OneCode.App.Services.Hooks.Notifications;
using OneCode.Core.Hooks.Notifications;

namespace OneCode.Tests;

/// <summary>
/// DeclarativeNotificationProvider 单测——钉钉官方签名向量逐字节验证（PR-3 首要验收），
/// 以及 body 模板渲染、成功判定、headers、异常路径。
/// </summary>
public sealed class DeclarativeNotificationProviderTests
{
    // 固定时钟：2026-01-01T00:00:00Z → ms = 1767225600000
    private static readonly DateTimeOffset FixedNow = DateTimeOffset.FromUnixTimeMilliseconds(1_767_225_600_000);
    private const string DingtalkSecret = "SEC922a15c5d51353d6cf2d3e26e2be1e12ea6a5b60fe335de8a3b5d757d30ffabb";

    // ---------- 钉钉签名向量 ----------

    [Fact]
    public async Task SendAsync_DingtalkPreset_SignatureMatchesOfficialAlgorithm()
    {
        // 钉钉官方加签算法：sign = urlencode(base64(hmac_sha256(key = timestampMs + "\n" + secret, message = "")))
        var handler = new TestingHttpHandler(HttpStatusCode.OK, """{"errcode":0,"errmsg":"ok"}""");
        var provider = CreateDingtalkProvider(handler);

        var result = await provider.SendAsync(
            new NotificationMessage { Text = "向量验证" },
            "https://oapi.dingtalk.com/robot/send?access_token=abc",
            DingtalkSecret,
            default);

        result.Success.Should().BeTrue();
        var query = handler.LastRequest!.RequestUri!.Query;

        // 时间戳参数为毫秒
        query.Should().Contain("timestamp=1767225600000");
        query.Should().Contain("access_token=abc", "既有查询参数必须保留");

        var stringToSign = "1767225600000\n" + DingtalkSecret;
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(stringToSign));
        var expectedSign = Uri.EscapeDataString(Convert.ToBase64String(hmac.ComputeHash([])));
        query.Should().Contain($"sign={expectedSign}",
            "钉钉签名向量: HMAC-SHA256(key=timestampMs+\\n+secret, message=\"\"), Base64 后 URL 编码");
    }

    [Fact]
    public async Task SendAsync_FeishuPresetVector_MatchesAlgorithm()
    {
        // 飞书：sign = base64(hmac_sha256(key = timestampS + "\n" + secret, message = ""))，Base64 原值经 query 转义
        var definition = new NotificationProviderDefinition
        {
            Url = "https://open.feishu.cn/hook",
            Body = TextBody("""{"content":"{{Text}}"}"""),
            Signing = new ProviderSignatureDefinition
            {
                KeyTemplate = "{Timestamp}\n{Secret}",
                MessageTemplate = string.Empty,
            },
            Success = SuccessDef("code", 0),
        };
        var handler = new TestingHttpHandler(HttpStatusCode.OK, """{"code":0,"msg":"ok"}""");
        var provider = new DeclarativeNotificationProvider("feishu", definition, new HttpClient(handler), clock: () => FixedNow);

        var result = await provider.SendAsync(
            new NotificationMessage { Text = "verify" }, definition.Url, "my-secret", default);

        result.Success.Should().BeTrue();
        var timestamp = "1767225600";
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(timestamp + "\nmy-secret"));
        var expectedSign = Uri.EscapeDataString(Convert.ToBase64String(hmac.ComputeHash([])));
        handler.LastRequest!.RequestUri!.Query.Should().Be($"?timestamp={timestamp}&sign={expectedSign}");
    }

    // ---------- body / headers 渲染 ----------

    [Fact]
    public async Task SendAsync_BodyTemplate_RendersTextIntoStringLeafOnly()
    {
        var definition = new NotificationProviderDefinition
        {
            Url = "https://example.com/send",
            Body = TextBody("""{"msgtype":"text","text":{"content":"{{Text}}"},"count":1,"nested":{"title":"{{Title}}"}}"""),
        };
        var handler = new TestingHttpHandler(HttpStatusCode.OK, """{"errcode":0}""");
        var provider = new DeclarativeNotificationProvider("x", definition, new HttpClient(handler), clock: () => FixedNow);

        await provider.SendAsync(new NotificationMessage { Text = "hi & <b>\"q\"</b>", Title = "T" }, null, null, default);

        // Text 含 JSON 特殊字符不得破坏 JSON 结构（渲染发生在字符串叶子内）
        using var body = JsonDocument.Parse(handler.LastRequestBody!);
        body.RootElement.GetProperty("text").GetProperty("content").GetString().Should().Be("hi & <b>\"q\"</b>");
        body.RootElement.GetProperty("nested").GetProperty("title").GetString().Should().Be("T");
        body.RootElement.GetProperty("count").GetInt32().Should().Be(1, "非字符串叶子保持原样");
    }

    [Fact]
    public async Task SendAsync_HeadersTemplate_RendersValues()
    {
        var definition = new NotificationProviderDefinition
        {
            Url = "https://example.com/send",
            Body = TextBody("""{"content":"{{Text}}"}"""),
            Headers = new Dictionary<string, string> { ["X-Event"] = "{{Event}}", ["X-Sign"] = "fixed" },
        };
        var handler = new TestingHttpHandler(HttpStatusCode.OK, "{}");
        var provider = new DeclarativeNotificationProvider("x", definition, new HttpClient(handler), clock: () => FixedNow);

        await provider.SendAsync(
            new NotificationMessage { Text = "m", Event = "Stop" }, null, null, default);

        handler.LastRequest!.Headers.GetValues("X-Event").Should().Contain("Stop");
        handler.LastRequest.Headers.GetValues("X-Sign").Should().Contain("fixed");
    }

    [Fact]
    public async Task SendAsync_HeaderPlacement_SignGoesToHeaderNotQuery()
    {
        var definition = new NotificationProviderDefinition
        {
            Url = "https://example.com/send",
            Body = TextBody("""{"content":"{{Text}}"}"""),
            Signing = new ProviderSignatureDefinition
            {
                KeyTemplate = "{Secret}",
                MessageTemplate = "{Timestamp}",
                Encoding = ProviderSignatureEncoding.Hex,
                Placement = ProviderSignaturePlacement.Header,
                ParamName = "X-Sign",
                TimestampParamName = "X-Ts",
            },
        };
        var handler = new TestingHttpHandler(HttpStatusCode.OK, "{}");
        var provider = new DeclarativeNotificationProvider("x", definition, new HttpClient(handler), clock: () => FixedNow);

        await provider.SendAsync(new NotificationMessage { Text = "m" }, null, "sec", default);

        handler.LastRequest!.RequestUri!.Query.Should().NotContain("sign");
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes("sec"));
        var expected = Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes("1767225600"))).ToLowerInvariant();
        handler.LastRequest.Headers.GetValues("X-Sign").Should().Contain(expected);
        handler.LastRequest.Headers.GetValues("X-Ts").Should().Contain("1767225600");
    }

    [Fact]
    public async Task SendAsync_SuccessFieldEqualsZero_ReturnsOk()
    {
        var handler = new TestingHttpHandler(HttpStatusCode.OK, """{"errcode":0,"errmsg":"ok"}""");
        var provider = CreateDingtalkProvider(handler);

        var result = await provider.SendAsync(new NotificationMessage { Text = "t" }, null, null, default);

        result.Success.Should().BeTrue();
    }

    [Fact]
    public async Task SendAsync_SuccessFieldNonZero_ReturnsFailWithMessage()
    {
        var handler = new TestingHttpHandler(HttpStatusCode.OK, """{"errcode":310000,"errmsg":"sign not match"}""");
        var provider = CreateDingtalkProvider(handler);

        var result = await provider.SendAsync(new NotificationMessage { Text = "t" }, null, null, default);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("310000");
        result.ErrorMessage.Should().Contain("sign not match");
    }

    [Fact]
    public async Task SendAsync_MissingSuccessField_ReturnsFail()
    {
        var handler = new TestingHttpHandler(HttpStatusCode.OK, """{"unexpected":true}""");
        var provider = CreateDingtalkProvider(handler);

        var result = await provider.SendAsync(new NotificationMessage { Text = "t" }, null, null, default);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("errcode");
    }

    [Fact]
    public async Task SendAsync_NonJsonResponse_TreatedAsFailure()
    {
        var handler = new TestingHttpHandler(HttpStatusCode.OK, "ok");
        var provider = CreateDingtalkProvider(handler);

        var result = await provider.SendAsync(new NotificationMessage { Text = "t" }, null, null, default);

        result.Success.Should().BeFalse("2xx 非 JSON 响应不能视为成功");
    }

    [Fact]
    public async Task SendAsync_WithoutSuccessDefinition_Http2xxIsEnough()
    {
        var definition = new NotificationProviderDefinition { Url = "https://example.com/send", Body = TextBody("""{"content":"{{Text}}"}""") };
        var handler = new TestingHttpHandler(HttpStatusCode.OK, "plain text");
        var provider = new DeclarativeNotificationProvider("x", definition, new HttpClient(handler), clock: () => FixedNow);

        var result = await provider.SendAsync(new NotificationMessage { Text = "t" }, null, null, default);

        result.Success.Should().BeTrue();
    }

    [Fact]
    public async Task SendAsync_HttpError_ReturnsFailWithStatusCode()
    {
        var handler = new TestingHttpHandler(HttpStatusCode.InternalServerError, "boom");
        var provider = CreateDingtalkProvider(handler);

        var result = await provider.SendAsync(new NotificationMessage { Text = "t" }, null, null, default);

        result.Success.Should().BeFalse();
        result.StatusCode.Should().Be(500);
    }

    [Fact]
    public async Task SendAsync_WebhookUrlFallsBackToDefinitionUrl()
    {
        var handler = new TestingHttpHandler(HttpStatusCode.OK, "{}");
        var provider = CreateDingtalkProvider(handler);

        await provider.SendAsync(new NotificationMessage { Text = "t" }, "  ", null, default);

        handler.LastRequest!.RequestUri!.GetLeftPart(UriPartial.Path)
            .Should().Be("https://oapi.dingtalk.com/robot/send");
    }

    [Fact]
    public async Task SendAsync_WithoutSecret_NoSignParams()
    {
        var handler = new TestingHttpHandler(HttpStatusCode.OK, """{"errcode":0}""");
        var provider = CreateDingtalkProvider(handler);

        await provider.SendAsync(new NotificationMessage { Text = "t" }, null, "", default);

        handler.LastRequest!.RequestUri!.Query.Should().NotContain("sign");
        handler.LastRequest.RequestUri.Query.Should().NotContain("timestamp");
    }

    // ---------- Helpers ----------

    private static DeclarativeNotificationProvider CreateDingtalkProvider(TestingHttpHandler handler) =>
        new(
            "dingtalk",
            new NotificationProviderDefinition
            {
                DisplayName = "钉钉机器人",
                Url = "https://oapi.dingtalk.com/robot/send",
                Body = TextBody("""{"msgtype":"text","text":{"content":"{{Text}}"}}"""),
                Signing = new ProviderSignatureDefinition
                {
                    KeyTemplate = "{TimestampMs}\n{Secret}",
                    MessageTemplate = string.Empty,
                    Encoding = ProviderSignatureEncoding.Base64UrlEncoded,
                    TimestampInMilliseconds = true,
                },
                Success = SuccessDef("errcode", 0),
            },
            new HttpClient(handler),
            clock: () => FixedNow);

    private static JsonElement TextBody(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private static ProviderSuccessDefinition SuccessDef(string field, int expected)
    {
        using var doc = JsonDocument.Parse(expected.ToString(CultureInfo.InvariantCulture));
        return new ProviderSuccessDefinition { Field = field, Expected = doc.RootElement.Clone() };
    }
}
