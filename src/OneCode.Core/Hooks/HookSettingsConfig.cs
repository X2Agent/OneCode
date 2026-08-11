using System.Text.Json.Serialization;

namespace OneCode.Core.Hooks;

/// <summary>
/// 匹配器分组：一个 matcher pattern 下的一组 hook 配置
/// </summary>
public sealed class HookMatcherGroup
{
    /// <summary>
    /// 匹配器 pattern。"" 或 "*" 匹配所有；"Bash" 精确匹配；"Bash*" 通配符；"Write|Read" 多值
    /// </summary>
    [JsonPropertyName("matcher")]
    public string Matcher { get; init; } = string.Empty;

    [JsonPropertyName("hooks")]
    public List<HookConfig> Hooks { get; init; } = new();
}

/// <summary>
/// 单个 Hook 的配置——Command / Notification / Http 类型的公共字段 + 类型特有字段
/// </summary>
public sealed class HookConfig
{
    /// <summary>Hook 类型：command / notification / http</summary>
    [JsonPropertyName("type")]
    public string? Type { get; init; }

    // 公共字段

    [JsonPropertyName("timeout")]
    public int? TimeoutMs { get; init; }

    [JsonPropertyName("once")]
    public bool Once { get; init; }

    [JsonPropertyName("statusMessage")]
    public string? StatusMessage { get; init; }

    [JsonPropertyName("priority")]
    public int? Priority { get; init; }

    // Command 类型字段

    [JsonPropertyName("command")]
    public string? Command { get; init; }

    [JsonPropertyName("shell")]
    public string? Shell { get; init; }

    // Notification 类型字段

    /// <summary>通知渠道 Provider 名称：feishu / wechat_work（未来可扩展 dingtalk / slack 等）</summary>
    [JsonPropertyName("provider")]
    public string? Provider { get; init; }

    /// <summary>Webhook URL（由 Provider 解释，通常为渠道提供的接入地址）</summary>
    [JsonPropertyName("webhookUrl")]
    public string? WebhookUrl { get; init; }

    /// <summary>签名密钥（用于 HMAC-SHA256 签名认证，可选）</summary>
    [JsonPropertyName("secret")]
    public string? Secret { get; init; }

    /// <summary>消息内容模板，支持 {{field}} 插值（如 {{Event}} / {{UserMessage}} / {{Timestamp}}）</summary>
    [JsonPropertyName("message")]
    public string? Message { get; init; }

    // Http 类型字段

    /// <summary>HTTP 请求方法：GET / POST / PUT / DELETE / PATCH（默认 POST）</summary>
    [JsonPropertyName("method")]
    public string? Method { get; init; }

    /// <summary>请求目标 URL（支持 {{field}} 插值，如 {{Cwd}}）</summary>
    [JsonPropertyName("url")]
    public string? Url { get; init; }

    /// <summary>自定义请求头（值为字符串模板，支持 {{field}} 插值）</summary>
    [JsonPropertyName("headers")]
    public Dictionary<string, string>? Headers { get; init; }

    /// <summary>请求体模板（POST/PUT/PATCH 使用，支持 {{field}} 插值；为 null 时无 body）</summary>
    [JsonPropertyName("body")]
    public string? Body { get; init; }
}
