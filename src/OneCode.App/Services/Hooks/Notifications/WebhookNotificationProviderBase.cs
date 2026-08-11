using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Web;
using OneCode.Core.Hooks.Notifications;

namespace OneCode.App.Services.Hooks.Notifications;

/// <summary>
/// Webhook 类通知渠道基类——飞书/企业微信/钉钉等共享相同流程：
///   1. 构造消息 payload（渠道特定字段名）
///   2. 若有 secret 则附加签名查询参数到 URL（渠道特定签名算法）
///   3. POST JSON 到 Webhook URL
///   4. 解析响应判断成功/失败（渠道特定响应字段名）
///
/// 子类只需重写：<see cref="BuildPayload"/>、<see cref="ComputeSign"/>、
/// 以及声明 <see cref="CodeFieldName"/>/<see cref="MsgFieldName"/>/<see cref="ProviderDisplayName"/>。
/// 共享流程（HTTP POST、签名 URL 构造、异常处理、状态码处理、响应解析）由此基类统一实现。
/// </summary>
public abstract class WebhookNotificationProviderBase(HttpClient httpClient, ILogger? logger = null) : INotificationProvider
{
    protected const string MsgTypeText = "text";
    protected const string SuccessCode = "0";

    protected HttpClient HttpClient { get; } = httpClient ?? throw new ArgumentNullException(nameof(httpClient));

    protected ILogger? Logger { get; } = logger;

    /// <inheritdoc />
    public abstract string Name { get; }

    /// <summary>响应 body 中表示状态码的 JSON 字段名（如 "code"、"errcode"）。</summary>
    protected abstract string CodeFieldName { get; }

    /// <summary>响应 body 中表示错误消息的 JSON 字段名（如 "msg"、"errmsg"）。</summary>
    protected abstract string MsgFieldName { get; }

    /// <summary>渠道显示名称，用于错误消息前缀（如 "Feishu"、"WeChatWork"）。</summary>
    protected abstract string ProviderDisplayName { get; }

    /// <summary>
    /// 构造渠道特定的消息 payload 对象。
    /// 基类将其序列化为 JSON 并 POST 到 Webhook URL。
    /// </summary>
    /// <param name="message">已填充内容的消息对象</param>
    /// <returns>可被 System.Text.Json 序列化的 payload 对象</returns>
    protected abstract object BuildPayload(NotificationMessage message);

    /// <summary>
    /// 计算签名——子类决定 HMAC 的 key 和 message 组合方式。
    /// 使用 <see cref="ComputeHmacSha256Base64"/> 辅助方法避免重复 HMAC 样板。
    /// </summary>
    /// <param name="timestamp">Unix 时间戳字符串</param>
    /// <param name="secret">签名密钥</param>
    /// <returns>Base64 编码的签名</returns>
    protected abstract string ComputeSign(string timestamp, string secret);

    /// <summary>
    /// 解析渠道响应 body，判断发送成功/失败。
    /// 默认实现读取 <see cref="CodeFieldName"/>/<see cref="MsgFieldName"/> 并与 <see cref="SuccessCode"/> 比较。
    /// 子类可 override 以处理非标准响应格式。
    /// </summary>
    protected virtual NotificationSendResult ParseResponse(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var code = doc.RootElement.TryGetProperty(CodeFieldName, out var codeEl)
                ? codeEl.GetInt32()
                : -1;
            var msg = doc.RootElement.TryGetProperty(MsgFieldName, out var msgEl)
                ? msgEl.GetString() ?? string.Empty
                : string.Empty;

            return code.ToString(CultureInfo.InvariantCulture) == SuccessCode
                ? NotificationSendResult.Ok()
                : NotificationSendResult.Fail($"{ProviderDisplayName} {CodeFieldName}={code}: {msg}");
        }
        catch (JsonException ex)
        {
            // HTTP 2xx 但 body 非 JSON（网关错误页、纯文本响应等）——不能假定成功，
            // 否则代理返回的错误页会被误报为送达。判定为失败并记录。
            Logger?.LogWarning(ex, "{ProviderName} webhook returned non-JSON body (treated as failure): {Preview}",
                Name, body.Length > 200 ? body[..200] : body);
            return NotificationSendResult.Fail($"{ProviderDisplayName} returned non-JSON response");
        }
    }

    /// <summary>
    /// HMAC-SHA256 + Base64 辅助方法，供子类 <see cref="ComputeSign"/> 复用。
    /// </summary>
    protected static string ComputeHmacSha256Base64(byte[] key, byte[] message)
    {
        using var hmac = new HMACSHA256(key);
        var hash = hmac.ComputeHash(message);
        return Convert.ToBase64String(hash);
    }

    /// <inheritdoc />
    public async Task<NotificationSendResult> SendAsync(
        NotificationMessage message, string webhookUrl, string? secret, CancellationToken ct)
    {
        var url = string.IsNullOrEmpty(secret) ? webhookUrl : BuildSignedUrl(webhookUrl, secret!);
        var payload = BuildPayload(message);

        try
        {
            using var response = await HttpClient.PostAsJsonAsync(url, payload, ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                Logger?.LogWarning("{ProviderName} webhook returned {Status}: {Body}",
                    Name, (int)response.StatusCode, errorBody);
                return NotificationSendResult.Fail(
                    $"HTTP {(int)response.StatusCode}: {errorBody}",
                    (int)response.StatusCode);
            }

            var responseBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return ParseResponse(responseBody);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Logger?.LogWarning(ex, "{ProviderName} notification failed", Name);
            return NotificationSendResult.Fail(ex.Message);
        }
    }

    /// <summary>
    /// 构造带签名的 Webhook URL——附加 timestamp + sign 查询参数。
    /// 签名值由子类的 <see cref="ComputeSign"/> 计算。
    /// </summary>
    protected string BuildSignedUrl(string webhookUrl, string secret)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var sign = ComputeSign(timestamp, secret);

        var uriBuilder = new UriBuilder(webhookUrl);
        var query = HttpUtility.ParseQueryString(uriBuilder.Query);
        query["timestamp"] = timestamp;
        query["sign"] = sign;
        uriBuilder.Query = query.ToString();
        return uriBuilder.ToString();
    }
}
