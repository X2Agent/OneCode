using System.Text.Json.Serialization;

namespace OneCode.Core.Hooks.Notifications;

/// <summary>HMAC-SHA256 签名值的编码方式。</summary>
public enum ProviderSignatureEncoding
{
    /// <summary>标准 Base64（飞书/企业微信）。</summary>
    [JsonStringEnumMemberName("base64")]
    Base64,

    /// <summary>标准 Base64 后再 URL 编码（钉钉）。</summary>
    [JsonStringEnumMemberName("base64-urlencoded")]
    Base64UrlEncoded,

    /// <summary>小写十六进制。</summary>
    [JsonStringEnumMemberName("hex")]
    Hex,
}

/// <summary>签名参数的附加位置。</summary>
public enum ProviderSignaturePlacement
{
    /// <summary>URL 查询参数。</summary>
    [JsonStringEnumMemberName("query")]
    Query,

    /// <summary>请求 Header（建议配合 base64/hex 编码使用）。</summary>
    [JsonStringEnumMemberName("header")]
    Header,
}

/// <summary>
/// 签名算法定义。模板变量集固定为 <c>{Secret}/{Timestamp}/{TimestampMs}</c>，
/// 禁止表达式求值——配置不能成为代码执行通道。
/// </summary>
public sealed record ProviderSignatureDefinition
{
    /// <summary>HMAC key 模板；空字符串表示 key 为空字节。</summary>
    public string KeyTemplate { get; init; } = string.Empty;

    /// <summary>HMAC message 模板；空字符串表示 message 为空字节（飞书/钉钉）。</summary>
    public string MessageTemplate { get; init; } = string.Empty;

    public ProviderSignatureEncoding Encoding { get; init; } = ProviderSignatureEncoding.Base64;

    public ProviderSignaturePlacement Placement { get; init; } = ProviderSignaturePlacement.Query;

    /// <summary>签名参数名（query 参数名或 header 名）。</summary>
    public string ParamName { get; init; } = "sign";

    /// <summary>时间戳参数名。</summary>
    public string TimestampParamName { get; init; } = "timestamp";

    /// <summary>true 时时间戳参数与 <c>{Timestamp}</c> 变量取毫秒（钉钉要求毫秒）；缺省 Unix 秒。</summary>
    [JsonPropertyName("timestampMs")]
    public bool TimestampInMilliseconds { get; init; }

    /// <summary>签名 preset 名（loader 层展开，见 <c>NotificationSignaturePresets</c>）；运行时为 null。</summary>
    public string? Preset { get; init; }
}

/// <summary>
/// 响应成功判定：顶层字段与期望值相等即成功（非 JSON 响应一律失败）。
/// 缺省（null）时仅 HTTP 2xx 即成功。
/// </summary>
public sealed record ProviderSuccessDefinition
{
    /// <summary>响应 body 顶层 JSON 字段名（如 "errcode"）。</summary>
    public required string Field { get; init; }

    /// <summary>期望值（任意 JSON 值，按 JSON 语义深度相等比较）。</summary>
    [JsonPropertyName("equals")]
    public required JsonElement Expected { get; init; }
}

/// <summary>
/// 声明式通知渠道定义——notification-providers.json 中每个渠道的描述。
/// 覆盖「固定端点 + 特定 payload + 签名变体」类渠道（钉钉/自建网关等），
/// 新增渠道无需写 C#；OAuth 等复杂集成仍走编译型 <see cref="INotificationProvider"/>。
/// </summary>
public sealed record NotificationProviderDefinition
{
    /// <summary>渠道显示名（诊断与 /hooks 展示用）。</summary>
    public string? DisplayName { get; init; }

    /// <summary>HTTP 方法，缺省 POST。</summary>
    public string Method { get; init; } = "POST";

    /// <summary>默认端点（必须 https）。发送时 HookConfig.WebhookUrl 优先于本值。</summary>
    public string Url { get; init; } = string.Empty;

    /// <summary>附加请求头；值支持消息模板（见 <see cref="NotificationProviderDefinition"/> 渲染规则）。</summary>
    public Dictionary<string, string>? Headers { get; init; }

    /// <summary>请求 body 模板（JSON 对象）；字符串叶子支持消息模板渲染。</summary>
    public JsonElement? Body { get; init; }

    /// <summary>签名算法定义（preset 已在加载期展开）。null 表示不签名。</summary>
    public ProviderSignatureDefinition? Signing { get; init; }

    /// <summary>响应成功判定。null 表示仅 HTTP 2xx 即成功。</summary>
    public ProviderSuccessDefinition? Success { get; init; }
}
