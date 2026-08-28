using System.Net;

namespace OneCode.App.Services.Hooks;

/// <summary>
/// 轻量异常 → StopFailure 类别映射器。
/// 类别集合与 HookEventMetadataRegistry.StopFailure 的 matcher 可选值对齐：
/// rate_limit / auth_failed / billing / invalid_request / server_error / max_output_tokens / unknown。
/// 判定顺序：消息级信号优先于泛化的状态码判定；沿异常链（含 AggregateException 展开）收集证据。
/// </summary>
public static class HookStopFailureClassifier
{
    /// <summary>限流（429 / rate limit）。</summary>
    public const string RateLimit = "rate_limit";

    /// <summary>认证失败（401/403 / invalid api key）。</summary>
    public const string AuthFailed = "auth_failed";

    /// <summary>计费问题（402 / quota / credit）。</summary>
    public const string Billing = "billing";

    /// <summary>请求非法（400/404/422 / invalid request）。</summary>
    public const string InvalidRequest = "invalid_request";

    /// <summary>服务端错误（5xx / overloaded / 网络超时）。</summary>
    public const string ServerError = "server_error";

    /// <summary>输出 token 上限。</summary>
    public const string MaxOutputTokens = "max_output_tokens";

    /// <summary>无法归类。</summary>
    public const string Unknown = "unknown";

    /// <summary>将异常归类为 StopFailure 类别常量（见类注释）。</summary>
    public static string Classify(Exception? exception)
    {
        if (exception is null)
            return Unknown;

        var chain = Flatten(exception).ToList();
        var combined = string.Join(" ", chain.Select(e => e.Message));
        var statuses = chain
            .OfType<HttpRequestException>()
            .Select(h => h.StatusCode)
            .Where(s => s.HasValue)
            .Select(s => s!.Value)
            .ToList();

        return ClassifyCore(combined, statuses);
    }

    private static string ClassifyCore(string message, IReadOnlyCollection<HttpStatusCode> statuses)
    {
        // 顺序敏感：特异性强的消息信号先判，避免被泛化的 5xx 信号吞掉。
        if (Matches(message, "max output tokens", "maximum output tokens", "max_output_tokens", "output token limit"))
            return MaxOutputTokens;
        if (Matches(message, "rate limit", "rate_limit", "ratelimit", "too many requests"))
            return RateLimit;
        if (Matches(message, "insufficient", "billing", "credit balance", "quota exceeded", "exceeded your current quota"))
            return Billing;
        if (Matches(message, "invalid api key", "invalid_api_key", "unauthorized", "authentication", "permission denied", "forbidden"))
            return AuthFailed;
        if (Matches(message, "invalid request", "invalid_request", "bad request", "unsupported", "not found"))
            return InvalidRequest;
        if (Matches(message, "overloaded", "overloaded_error", "internal server error", "server error", "server had an error", "service unavailable", "bad gateway", "timeout", "timed out"))
            return ServerError;

        if (statuses.Contains(HttpStatusCode.TooManyRequests))
            return RateLimit;
        if (statuses.Contains(HttpStatusCode.PaymentRequired))
            return Billing;
        if (statuses.Contains(HttpStatusCode.Unauthorized) || statuses.Contains(HttpStatusCode.Forbidden))
            return AuthFailed;
        if (statuses.Contains(HttpStatusCode.BadRequest)
            || statuses.Contains(HttpStatusCode.NotFound)
            || statuses.Contains(HttpStatusCode.UnprocessableEntity))
            return InvalidRequest;
        if (statuses.Any(s => (int)s >= 500))
            return ServerError;

        return Unknown;
    }

    private static bool Matches(string message, params string[] signals) =>
        signals.Any(s => message.Contains(s, StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<Exception> Flatten(Exception exception)
    {
        if (exception is AggregateException aggregate)
        {
            var inners = aggregate.InnerExceptions.SelectMany(Flatten).ToList();
            if (inners.Count > 0)
            {
                // AggregateException 自身的 "One or more errors occurred" 是噪音，丢弃。
                foreach (var inner in inners)
                    yield return inner;
                yield break;
            }
        }

        yield return exception;
        if (exception.InnerException is { } innerException)
        {
            foreach (var nested in Flatten(innerException))
                yield return nested;
        }
    }
}
