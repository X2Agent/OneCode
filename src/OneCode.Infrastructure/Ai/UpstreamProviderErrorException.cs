namespace OneCode.Infrastructure.Ai;


/// <summary>
/// Provider 在响应体中返回显式错误对象（<c>{"error":{...}}</c>）时抛出。
/// 部分网关过载时不返回 4xx/5xx 而是 200/400 + 错误体，且把上游真实错误掩码为
/// 不透明码。<see cref="IsTransient"/> 判定瞬时性：瞬时错误由
/// <see cref="RetryOnOverloadChatClient"/> 自动重试，永久错误立即失败。
/// </summary>
public sealed class UpstreamProviderErrorException(string message, string? errorCode = null, int? httpStatusCode = null)
    : Exception(message)
{
    /// <summary>上游错误体中的原始错误码（可能是数字字符串，也可能是文本码）。</summary>
    public string? UpstreamErrorCode { get; } = errorCode;

    /// <summary>承载该错误体的 HTTP 状态码（网关外壳状态，可能与上游真实状态不一致）。</summary>
    public int? HttpStatusCode { get; } = httpStatusCode;

    /// <summary>是否为可重试的瞬时上游故障（过载 / 限流 / 5xx 等）。</summary>
    public bool IsTransient { get; } = ClassifyTransient(errorCode, message, httpStatusCode);

    /// <summary>
    /// 瞬时性判定（单一规则来源，Handler 日志与重试中间件均复用）。
    /// 核心不变量：网关中继/掩码类错误默认瞬时（可重试），仅永久性证据
    /// （无效密钥/配额/上下文超长等）快速失败。
    /// </summary>
    public static bool ClassifyTransient(string? errorCode, string message, int? httpStatusCode = null)
    {
        if (int.TryParse(errorCode, out var numeric))
            return numeric is 408 or 429 || (numeric is >= 500 and <= 599);

        if (errorCode is not null && PermanentErrorCodes.Contains(errorCode))
            return false;

        if (ContainsPermanentSignature($"{errorCode} {message}"))
            return false;

        if (httpStatusCode is 408 or 429 or >= 500 and <= 599)
            return true;

        if (errorCode is not null && GatewayRelayErrorCodes.Contains(errorCode))
            return true;

        var haystack = $"{errorCode} {message}";
        if (haystack.Contains("overload", StringComparison.OrdinalIgnoreCase)
            || haystack.Contains("temporarily", StringComparison.OrdinalIgnoreCase)
            || haystack.Contains("rate limit", StringComparison.OrdinalIgnoreCase)
            || haystack.Contains("timeout", StringComparison.OrdinalIgnoreCase)
            || haystack.Contains("unavailable", StringComparison.OrdinalIgnoreCase)
            || haystack.Contains("capacity", StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

    private static readonly HashSet<string> PermanentErrorCodes = new(StringComparer.Ordinal)
    {
        // OpenRouter 规范码（error.metadata.error_type）
        "context_length_exceeded",
        "authentication",
        "payment_required",
        "content_policy_violation",
        "refusal",
        // OpenAI 风格 type
        "invalid_request_error",
        "model_not_found",
        "permission_error",
        "not_found_error",
        // new-api / one-api 系与常见网关
        "insufficient_quota",
        "invalid_api_key",
        "billing_hard_limit_reached",
        "model_not_exists",
    };

    /// <summary>网关中继/不透明错误码：真实原因被掩码，默认瞬时（可重试）。</summary>
    private static readonly HashSet<string> GatewayRelayErrorCodes = new(StringComparer.Ordinal)
    {
        // new-api / one-api 系中继错误
        "bad_response_status_code",
        "bad_response",
        "upstream_error",
        "do_request_failed",
        "read_response_body_failed",
        "new_api_error",
        "openai_error",
        // 服务端内部错误
        "api_error",
        "server_error",
        "internal_error",
        "unmapped",
        "provider_error",
        // OpenRouter 瞬时类规范码
        "provider_overloaded",
        "provider_unavailable",
        "rate_limit_exceeded",
        "timeout",
    };

    /// <summary>永久性签名关键词：命中即判永久。</summary>
    private static readonly string[] PermanentSignatures =
    [
        "invalid api key",
        "invalid key",
        "unauthorized",
        "authentication",
        "forbidden",
        "permission denied",
        "insufficient quota",
        "quota exceeded",
        "billing",
        "credit balance",
        "payment required",
        "context length",
        "maximum context",
        "too many tokens",
        "model not found",
        "no such model",
        "does not exist",
        "content policy",
        "content filter",
        "content filtering",
        "safety system",
    ];

    private static bool ContainsPermanentSignature(string haystack)
    {
        var normalized = haystack.Replace('_', ' ');
        foreach (var signature in PermanentSignatures)
        {
            if (normalized.Contains(signature, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}
