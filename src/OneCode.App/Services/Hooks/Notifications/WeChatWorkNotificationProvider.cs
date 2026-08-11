using System.Text;
using OneCode.Core.Hooks.Notifications;

namespace OneCode.App.Services.Hooks.Notifications;

/// <summary>
/// 企业微信群机器人通知 Provider
///
/// 消息格式：{"msgtype":"text","text":{"content":"消息内容"}}
/// 签名算法：HMAC-SHA256(key = secret, message = timestamp + "\n" + secret)，Base64 编码
/// 签名附加：URL 查询参数 timestamp + sign
///
/// 配置示例（hooks.json）：
/// {
///   "provider": "wechat_work",
///   "webhookUrl": "https://qyapi.weixin.qq.com/cgi-bin/webhook/send?key=xxx",
///   "secret": "your-sign-secret",
///   "message": "[OneCode] 事件 {{Event}} 触发于 {{Timestamp}}"
/// }
/// </summary>
public sealed class WeChatWorkNotificationProvider(HttpClient httpClient, ILogger<WeChatWorkNotificationProvider>? logger = null)
    : WebhookNotificationProviderBase(httpClient, logger)
{
    /// <inheritdoc />
    public override string Name => "wechat_work";

    /// <inheritdoc />
    protected override string CodeFieldName => "errcode";

    /// <inheritdoc />
    protected override string MsgFieldName => "errmsg";

    /// <inheritdoc />
    protected override string ProviderDisplayName => "WeChatWork";

    /// <inheritdoc />
    protected override object BuildPayload(NotificationMessage message) => new
    {
        msgtype = MsgTypeText,
        text = new { content = message.Text },
    };

    /// <inheritdoc />
    /// <remarks>
    /// 企业微信签名：HMAC-SHA256(key = secret, message = timestamp + "\n" + secret)
    /// </remarks>
    protected override string ComputeSign(string timestamp, string secret)
    {
        var keyBytes = Encoding.UTF8.GetBytes(secret);
        var messageBytes = Encoding.UTF8.GetBytes(timestamp + "\n" + secret);
        return ComputeHmacSha256Base64(keyBytes, messageBytes);
    }
}
