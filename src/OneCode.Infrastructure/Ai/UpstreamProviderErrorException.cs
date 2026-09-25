namespace OneCode.Infrastructure.Ai;


/// <summary>
/// Provider 在响应体中返回显式错误对象（<c>{"error":{...}}</c>）时抛出。
/// 部分网关过载时不返回 4xx/5xx 而是 200/400 + 错误体，且把上游真实错误掩码为
/// 不透明码。<see cref="IsTransient"/> 判定瞬时性：瞬时错误由
/// <see cref="RetryOnOverloadChatClient"/> 自动重试，永久错误立即失败。
/// <see cref="ErrorType"/> 为 OpenRouter 规范类型码，优先于 HTTP 外壳状态判定。
/// </summary>
public sealed class UpstreamProviderErrorException(
    string message,
    string? errorCode = null,
    int? httpStatusCode = null,
    string? errorType = null,
    TimeSpan? retryAfter = null)
    : Exception(message)
{
    /// <summary>上游错误体中的原始错误码（可能是数字字符串，也可能是文本码）。</summary>
    public string? UpstreamErrorCode { get; } = errorCode;

    /// <summary>承载该错误体的 HTTP 状态码（网关外壳状态，可能与上游真实状态不一致）。</summary>
    public int? HttpStatusCode { get; } = httpStatusCode;

    /// <summary>OpenRouter 规范错误类型码（<c>error.metadata.error_type</c>），非 OpenRouter 网关为空。</summary>
    public string? ErrorType { get; } = errorType;

    /// <summary>响应头 <c>Retry-After</c> 给出的建议等待时间（429/503 时 OpenRouter 会带）。</summary>
    public TimeSpan? RetryAfter { get; } = retryAfter;

    /// <summary>是否为可重试的瞬时上游故障（过载 / 限流 / 5xx 等）。</summary>
    public bool IsTransient { get; } = ClassifyTransient(errorCode, message, httpStatusCode, errorType);

    /// <summary>
    /// 瞬时性判定（单一规则来源，Handler 日志与重试中间件均复用）。
    /// 类型码优先于外壳状态码，其余规则不变。
    /// </summary>
    public static bool ClassifyTransient(
        string? errorCode, string message, int? httpStatusCode = null, string? errorType = null)
    {
        if (errorType is not null)
        {
            if (TransientErrorTypes.Contains(errorType))
                return true;
            if (PermanentErrorTypes.Contains(errorType))
                return false;
        }

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

    /// <summary>OpenRouter 规范类型码 → 瞬时（限流 / 过载 / 上游不可用 / 服务端）。</summary>
    private static readonly HashSet<string> TransientErrorTypes = new(StringComparer.Ordinal)
    {
        "rate_limit_exceeded",
        "provider_overloaded",
        "provider_unavailable",
        "timeout",
        "server",
        "unmapped",
    };

    /// <summary>
    /// OpenRouter 规范类型码 → 永久：这些错误同样可能用 429/400 外壳返回，重试无意义。
    /// </summary>
    private static readonly HashSet<string> PermanentErrorTypes = new(StringComparer.Ordinal)
    {
        // 计费与鉴权
        "payment_required",
        "authentication",
        "permission_denied",
        // 长度与预算（重试不改变 token 预算）
        "context_length_exceeded",
        "max_tokens_exceeded",
        "token_limit_exceeded",
        "string_too_long",
        // 请求校验
        "invalid_request",
        "invalid_prompt",
        "not_found",
        "precondition_failed",
        "payload_too_large",
        "unprocessable",
        // 内容策略
        "content_policy_violation",
        "refusal",
        // 图片
        "invalid_image",
        "image_too_large",
        "image_too_small",
        "unsupported_image_format",
        "image_not_found",
        "image_download_failed",
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
        // OpenRouter 瞬时代码也可能落在 errorCode 字段，与类型码集合兜底同一词汇表。
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

    /// <summary>
    /// 按 OpenRouter 类型码给出可执行的修复提示，直接拼进 Exception.Message——
    /// TUI 错误块与通知都以 Message 呈现。无类型码时按上游原文签名兜底。
    /// </summary>
    public static string? BuildHint(string? errorType, string? upstreamMessage = null) => errorType switch
    {
        "payment_required" =>
            "Insufficient credits — top up your provider balance (OpenRouter: https://openrouter.ai/credits) and retry.",
        "authentication" =>
            "API key rejected by provider — check the key (OpenRouter: https://openrouter.ai/keys).",
        "permission_denied" =>
            "Key lacks permission for this model, or a guardrail blocked the request — check key/model permissions.",
        "context_length_exceeded" or "token_limit_exceeded" or "string_too_long" =>
            "Context window exceeded — run /compact or switch to a larger-context model.",
        "max_tokens_exceeded" =>
            "Output budget exhausted before completion — raise max output tokens or cap reasoning effort.",
        "rate_limit_exceeded" =>
            "Rate limited by provider — wait for the Retry-After window, or switch provider/model (OpenRouter :free models have tight daily caps).",
        "provider_overloaded" or "provider_unavailable" =>
            "Upstream provider temporarily unavailable — retry shortly or switch provider/model.",
        "timeout" =>
            "Provider timed out — retry, or switch to a faster provider/model.",
        "content_policy_violation" or "refusal" =>
            "Request declined by content policy — rephrase the prompt or switch model.",
        "invalid_request" or "invalid_prompt" or "unprocessable" =>
            "Provider rejected the request payload — check the model ID and options (/config).",
        "not_found" =>
            "Model not found on provider — check the model ID (OpenRouter uses vendor/model slugs).",
        _ when IsSharedPoolRateLimit(upstreamMessage) =>
            "The model's upstream shared pool is rate-limited (typical for :free models) — run /config set global model <other-model>, add your own provider key via OpenRouter integrations, or retry later.",
        _ => null,
    };

    /// <summary>
    /// OpenRouter 对免费模型共享池限流的原文签名（无 error_type 的旧式掩码体）。
    /// 与账号额度无关，重试无用，必须换模型或绑自有 key。
    /// </summary>
    private static bool IsSharedPoolRateLimit(string? message) =>
        message is not null && (
            message.Contains("rate-limited upstream", StringComparison.OrdinalIgnoreCase)
            || message.Contains("shared pool", StringComparison.OrdinalIgnoreCase));
}
