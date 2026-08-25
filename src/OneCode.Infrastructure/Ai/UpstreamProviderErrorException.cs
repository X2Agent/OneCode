namespace OneCode.Infrastructure.Ai;


/// <summary>
/// Provider 在 HTTP 200 的响应体中返回了显式错误对象（<c>{"error":{...}}</c>）时抛出。
/// 一些 OpenAI 兼容网关（OpenRouter → 上游 Nvidia 等）过载时不返回 4xx/5xx 状态码，
/// 而是 200 + 错误体。本异常保留上游真实的错误消息与错误码，避免被笼统地
/// 归类为「empty choices」而误导排查。
///
/// <see cref="IsTransient"/> 依据上游错误码/消息判断是否为瞬时故障：
/// 瞬时错误由 <see cref="RetryOnOverloadChatClient"/> 自动重试；
/// 非瞬时错误（如 401 无效密钥、400 参数错误）立即失败，不浪费重试预算。
/// </summary>
public sealed class UpstreamProviderErrorException(string message, string? errorCode = null)
    : Exception(message)
{
    /// <summary>上游错误体中的原始错误码（可能是数字字符串，也可能是文本码）。</summary>
    public string? UpstreamErrorCode { get; } = errorCode;

    /// <summary>是否为可重试的瞬时上游故障（过载 / 限流 / 5xx 等）。</summary>
    public bool IsTransient { get; } = ClassifyTransient(errorCode, message);

    /// <summary>
    /// 瞬时性判定（单一规则来源，Handler 日志与重试中间件均复用）：
    /// 数字错误码 408/429/5xx → 瞬时；其余数字码（400/401/403…）→ 永久；
    /// 非数字错误码将错误码与消息合并后按关键词（overloaded / temporarily / rate limit…）判断。
    /// </summary>
    public static bool ClassifyTransient(string? errorCode, string message)
    {
        if (int.TryParse(errorCode, out var numeric))
            return numeric is 408 or 429 || (numeric is >= 500 and <= 599);

        // 错误码本身可能是文本码（如 "overloaded"、"insufficient_quota"），与消息一起参与判定。
        var haystack = $"{errorCode} {message}";
        return haystack.Contains("overload", StringComparison.OrdinalIgnoreCase)
            || haystack.Contains("temporarily", StringComparison.OrdinalIgnoreCase)
            || haystack.Contains("rate limit", StringComparison.OrdinalIgnoreCase)
            || haystack.Contains("timeout", StringComparison.OrdinalIgnoreCase)
            || haystack.Contains("unavailable", StringComparison.OrdinalIgnoreCase)
            || haystack.Contains("capacity", StringComparison.OrdinalIgnoreCase);
    }
}
