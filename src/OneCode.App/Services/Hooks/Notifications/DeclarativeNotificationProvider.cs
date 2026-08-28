using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using OneCode.Core.Hooks.Notifications;

namespace OneCode.App.Services.Hooks.Notifications;

/// <summary>
/// 声明式通知渠道引擎——唯一运行时类，把 <see cref="NotificationProviderDefinition"/> 转换为
/// HTTP 发送行为（消息渲染 → 签名 → 请求 → 响应成功判定）。
///
/// 消息模板（{{Text}}/{{Title}}/{{Event}}/{{Timestamp}}，兼容单花括号）应用于 body 字符串叶子
/// 与 headers 值；第一级 payload 渲染已由 NotificationHookExecutor 完成。
/// 签名模板使用单花括号变量（{Secret}/{Timestamp}/{TimestampMs}），命名空间与消息模板隔离。
/// HttpClient 来自 IHttpClientFactory（命名客户端），由 <see cref="NotificationProviderRegistry"/> 注入。
/// </summary>
public sealed partial class DeclarativeNotificationProvider : INotificationProvider
{
    private readonly NotificationProviderDefinition _definition;
    private readonly HttpClient _httpClient;
    private readonly ILogger? _logger;
    private readonly Func<DateTimeOffset> _clock;

    public DeclarativeNotificationProvider(
        string name,
        NotificationProviderDefinition definition,
        HttpClient httpClient,
        ILogger? logger = null,
        Func<DateTimeOffset>? clock = null)
    {
        Name = string.IsNullOrWhiteSpace(name) ? throw new ArgumentException("Provider name is required", nameof(name)) : name;
        _definition = definition ?? throw new ArgumentNullException(nameof(definition));
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _logger = logger;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public async Task<NotificationSendResult> SendAsync(
        NotificationMessage message, string webhookUrl, string? secret, CancellationToken ct)
    {
        try
        {
            var now = _clock();
            var timestampSeconds = now.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
            var timestampMilliseconds = now.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
            var timestamp = _definition.Signing is { TimestampInMilliseconds: true }
                ? timestampMilliseconds
                : timestampSeconds;

            var sign = ComputeSign(timestampSeconds, timestampMilliseconds, secret);
            var url = BuildUrl(
                string.IsNullOrWhiteSpace(webhookUrl)
                    ? HookSecretExpander.Expand(_definition.Url) ?? _definition.Url
                    : webhookUrl,
                timestamp, sign);

            using var request = new HttpRequestMessage(new HttpMethod(_definition.Method), url)
            {
                Content = new StringContent(RenderBody(message), Encoding.UTF8, "application/json"),
            };
            if (_definition.Headers is not null)
            {
                foreach (var (headerName, headerValue) in _definition.Headers)
                    request.Headers.TryAddWithoutValidation(headerName, RenderMessageTemplate(headerValue, message));
            }

            // Header 放置：签名与时间戳以 Header 附加
            var signing = _definition.Signing;
            if (signing is not null && signing.Placement == ProviderSignaturePlacement.Header && sign.Length > 0)
            {
                request.Headers.TryAddWithoutValidation(signing.TimestampParamName, timestamp);
                request.Headers.TryAddWithoutValidation(signing.ParamName, sign);
            }


            using var response = await _httpClient.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                _logger?.LogWarning("{ProviderName} webhook returned {Status}: {Body}",
                    Name, (int)response.StatusCode, errorBody);
                return NotificationSendResult.Fail($"HTTP {(int)response.StatusCode}: {errorBody}", (int)response.StatusCode);
            }

            var responseBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return ParseResponse(responseBody);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "{ProviderName} notification failed", Name);
            return NotificationSendResult.Fail(ex.Message);
        }
    }

    /// <summary>计算 HMAC-SHA256 签名值（key/message 由模板展开，编码由定义决定）。</summary>
    private string ComputeSign(string timestampSeconds, string timestampMilliseconds, string? secret)
    {
        var signing = _definition.Signing;
        if (signing is null || string.IsNullOrWhiteSpace(secret))
            return string.Empty;

        var key = RenderSigningTemplate(signing.KeyTemplate, timestampSeconds, timestampMilliseconds, secret);
        var msg = RenderSigningTemplate(signing.MessageTemplate, timestampSeconds, timestampMilliseconds, secret);
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(key));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(msg));
        return signing.Encoding switch
        {
            ProviderSignatureEncoding.Hex => Convert.ToHexString(hash).ToLowerInvariant(),
            ProviderSignatureEncoding.Base64UrlEncoded => Uri.EscapeDataString(Convert.ToBase64String(hash)),
            _ => Convert.ToBase64String(hash),
        };
    }

    /// <summary>构造请求 URL；query 放置时手动拼接以避免签名值被二次编码。</summary>
    private string BuildUrl(string baseUrl, string timestamp, string sign)
    {
        var signing = _definition.Signing;
        if (signing is null || sign.Length == 0)
            return baseUrl;

        if (signing.Placement == ProviderSignaturePlacement.Header)
            return baseUrl;

        var separator = baseUrl.Contains('?', StringComparison.Ordinal) ? "&" : "?";
        var signValue = signing.Encoding == ProviderSignatureEncoding.Base64UrlEncoded
            ? sign // 已是 URL 编码后的 Base64（钉钉语义），不再二次编码
            : Uri.EscapeDataString(sign);
        return $"{baseUrl}{separator}" +
               $"{Uri.EscapeDataString(signing.TimestampParamName)}={Uri.EscapeDataString(timestamp)}&" +
               $"{Uri.EscapeDataString(signing.ParamName)}={signValue}";
    }

    /// <summary>渲染 body 模板：仅替换字符串叶子，保持 JSON 结构与其他类型值原样。</summary>
    private string RenderBody(NotificationMessage message)
    {
        if (_definition.Body is not { ValueKind: JsonValueKind.Object } body)
            return "{}";

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
            WriteRendered(body, writer, message);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteRendered(JsonElement element, Utf8JsonWriter writer, NotificationMessage message)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject())
                {
                    writer.WritePropertyName(property.Name);
                    WriteRendered(property.Value, writer, message);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                    WriteRendered(item, writer, message);
                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(RenderMessageTemplate(element.GetString() ?? string.Empty, message));
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }

    /// <summary>解析响应并按定义判定成功/失败；有 Success 定义时非 JSON 响应一律失败，无定义时仅看 HTTP 2xx。</summary>
    private NotificationSendResult ParseResponse(string body)
    {
        var success = _definition.Success;
        if (success is null)
            return NotificationSendResult.Ok();

        try
        {
            using var doc = JsonDocument.Parse(body);
            var displayName = _definition.DisplayName ?? Name;
            if (!doc.RootElement.TryGetProperty(success.Field, out var actual))
                return NotificationSendResult.Fail($"{displayName}: 响应缺少字段 '{success.Field}'（{Preview(body)}）");

            return JsonElement.DeepEquals(actual, success.Expected)
                ? NotificationSendResult.Ok()
                : NotificationSendResult.Fail($"{displayName} {success.Field}={actual}（{Preview(body)}）");
        }
        catch (JsonException ex)
        {
            // HTTP 2xx 但 body 非 JSON（网关错误页等）不能假定成功
            _logger?.LogWarning(ex, "{ProviderName} webhook returned non-JSON body (treated as failure)", Name);
            return NotificationSendResult.Fail($"{_definition.DisplayName ?? Name} returned non-JSON response");
        }
    }

    private static string Preview(string body) => body.Length > 200 ? body[..200] : body;

    /// <summary>签名模板渲染：单花括号变量 {Secret}/{Timestamp}/{TimestampMs}，未知占位符保持原样。</summary>
    private static string RenderSigningTemplate(
        string template, string timestampSeconds, string timestampMilliseconds, string secret) =>
        template
            .Replace("{Secret}", secret, StringComparison.Ordinal)
            .Replace("{TimestampMs}", timestampMilliseconds, StringComparison.Ordinal)
            .Replace("{Timestamp}", timestampSeconds, StringComparison.Ordinal);

    /// <summary>消息模板渲染：{{Field}} 与 {Field} 两种写法，未知占位符保持原样。</summary>
    private static string RenderMessageTemplate(string template, NotificationMessage message) =>
        MessagePattern().Replace(template, match =>
        {
            var field = match.Groups[1].Value;
            return field switch
            {
                "Text" => message.Text,
                "Title" => message.Title ?? string.Empty,
                "Event" => message.Event ?? string.Empty,
                "Timestamp" => message.Timestamp.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                _ => match.Value,
            };
        });

    [GeneratedRegex(@"\{\{?(\w+)\}?\}")]
    private static partial Regex MessagePattern();
}
