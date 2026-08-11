using System.Text;
using OneCode.Core.Hooks.Notifications;

namespace OneCode.App.Services.Hooks.Notifications;

/// <summary>
/// 飞书机器人通知 Provider
///
/// 消息格式：{"msg_type":"text","content":{"text":"消息内容"}}
/// 签名算法：HMAC-SHA256(key = timestamp + "\n" + secret, message = "")，Base64 编码
/// 签名附加：URL 查询参数 timestamp + sign
///
/// 配置示例（hooks.json）：
/// {
///   "provider": "feishu",
///   "webhookUrl": "https://open.feishu.cn/open-apis/bot/v2/hook/xxx",
///   "secret": "your-sign-secret",
///   "message": "[OneCode] 事件 {{Event}} 触发于 {{Timestamp}}"
/// }
/// </summary>
public sealed class FeishuNotificationProvider(HttpClient httpClient, ILogger<FeishuNotificationProvider>? logger = null)
    : WebhookNotificationProviderBase(httpClient, logger)
{
    /// <inheritdoc />
    public override string Name => "feishu";

    /// <inheritdoc />
    protected override string CodeFieldName => "code";

    /// <inheritdoc />
    protected override string MsgFieldName => "msg";

    /// <inheritdoc />
    protected override string ProviderDisplayName => "Feishu";

    /// <inheritdoc />
    protected override object BuildPayload(NotificationMessage message) => new
    {
        msg_type = MsgTypeText,
        content = new { text = message.Text },
    };

    /// <inheritdoc />
    /// <remarks>
    /// 飞书签名：HMAC-SHA256(key = timestamp + "\n" + secret, message = "")
    /// </remarks>
    protected override string ComputeSign(string timestamp, string secret)
    {
        var keyBytes = Encoding.UTF8.GetBytes(timestamp + "\n" + secret);
        return ComputeHmacSha256Base64(keyBytes, Array.Empty<byte>());
    }
}
