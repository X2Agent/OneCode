using OneCode.Core.Hooks.Notifications;

namespace OneCode.App.Services.Hooks.Notifications;

/// <summary>
/// 签名算法 preset 库——官方签名向量的配置化表达，由声明式 Provider Loader 在加载期展开。
///
/// 变量集固定 <c>{Secret}/{Timestamp}/{TimestampMs}</c>（Unix 秒 / 毫秒），禁止表达式求值。
/// 算法逐字节对照（官方向量见 DeclarativeNotificationProviderTests）：
///   - 飞书：key = timestamp + "\n" + secret（秒），message 为空，Base64；
///   - 企业微信：key = secret，message = timestamp + "\n" + secret（秒），Base64；
///   - 钉钉：key = timestampMs + "\n" + secret（毫秒），message 为空，Base64 后 URL 编码。
/// </summary>
internal static class NotificationSignaturePresets
{
    /// <summary>可用 preset 名（错误提示用）。</summary>
    public static readonly IReadOnlyList<string> Known = ["feishu", "wechat_work", "dingtalk"];

    /// <summary>展开 preset 为签名定义；未知 preset 返回 null。</summary>
    public static ProviderSignatureDefinition? Expand(string? preset) => preset?.ToLowerInvariant() switch
    {
        "feishu" => new ProviderSignatureDefinition
        {
            KeyTemplate = "{Timestamp}\n{Secret}",
            MessageTemplate = string.Empty,
        },
        "wechat_work" => new ProviderSignatureDefinition
        {
            KeyTemplate = "{Secret}",
            MessageTemplate = "{Timestamp}\n{Secret}",
        },
        "dingtalk" => new ProviderSignatureDefinition
        {
            KeyTemplate = "{TimestampMs}\n{Secret}",
            MessageTemplate = string.Empty,
            Encoding = ProviderSignatureEncoding.Base64UrlEncoded,
            TimestampInMilliseconds = true,
        },
        _ => null,
    };
}
