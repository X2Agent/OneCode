namespace OneCode.Infrastructure.Ai;

/// <summary>
/// Sanitizes JSON and SSE responses from OpenAI-compatible APIs before the official
/// OpenAI .NET SDK deserializes them. Common vendor mismatches:
/// <list type="bullet">
/// <item><c>tool_calls</c> / <c>annotations</c> sent as <c>null</c> instead of <c>[]</c></item>
/// <item><c>finish_reason</c> sent as <c>""</c> or a vendor alias instead of an official enum value</item>
/// <item>gateways (OpenRouter) masking upstream provider errors as generic bodies
/// (<c>429 "Provider returned error"</c>) — the real cause lives in <c>error.metadata</c>
/// and is folded into <see cref="UpstreamProviderErrorException"/> with the
/// <c>Retry-After</c> header</item>
/// </list>
/// </summary>
public sealed class OpenAiResponseSanitizingHandler : DelegatingHandler
{
    private readonly ILogger<OpenAiResponseSanitizingHandler>? _logger;

    /// <summary>
    /// DI / <see cref="IHttpClientFactory"/> constructor — InnerHandler is set by the pipeline.
    /// </summary>
    public OpenAiResponseSanitizingHandler(ILogger<OpenAiResponseSanitizingHandler>? logger = null)
    {
        _logger = logger;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.Content is null)
            return response;

        var mediaType = response.Content.Headers.ContentType?.MediaType;

        if (string.Equals(mediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase))
        {
            response.Content = new OpenAiSseSanitizingContent(response.Content);
            return response;
        }

        if (!string.Equals(mediaType, "application/json", StringComparison.OrdinalIgnoreCase))
            return response;

        return await SanitizeJsonResponseAsync(response, cancellationToken).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> SanitizeJsonResponseAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var body = await response.Content!.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var statusCode = (int)response.StatusCode;

        // 网关过载时常返回 200/400 + 错误体，并把上游真实错误掩码为不透明码
        // （如 "openai_error" / "bad_response_status_code"）。OpenRouter 类型码优先于
        // HTTP 状态判定瞬时性：余额不足/上下文超长也包在 429/400 外壳里。
        if (OpenAiResponseSanitizer.TryExtractUpstreamError(body, out var upstreamMessage, out var upstreamCode, out var errorType))
        {
            var preview = Preview(body);
            _logger?.LogWarning(
                "Provider returned HTTP {Status} with an upstream error body — treating as {Kind}. Body: {Body}",
                statusCode,
                UpstreamProviderErrorException.ClassifyTransient(upstreamCode, upstreamMessage, statusCode, errorType)
                    ? "transient (retryable)"
                    : "permanent (fail fast)",
                preview);

            var retryAfter = ReadRetryAfter(response);
            var baseMessage =
                $"Provider returned HTTP {statusCode} with an upstream error body: \"{upstreamMessage}\""
                + (upstreamCode is null ? "" : $" (code: {upstreamCode})");
            var message = UpstreamProviderErrorException.BuildHint(errorType, upstreamMessage) is { } hint
                ? string.Concat(baseMessage, " — ", hint)
                : baseMessage;

            throw new UpstreamProviderErrorException(
                message,
                upstreamCode,
                statusCode,
                errorType,
                retryAfter);
        }

        // 200 + 空 choices：SDK 反序列化会在 ChatCompletion.get_Role() 内部越界崩溃，
        // 转为专用异常交由 RetryOnOverloadChatClient 按瞬时上游错误重试。
        if (OpenAiResponseSanitizer.HasEmptyChoices(body))
        {
            var preview = Preview(body);
            _logger?.LogWarning(
                "Provider returned HTTP {Status} with empty choices — treating as transient upstream error. Body: {Body}",
                statusCode, preview);
            throw new EmptyChoicesResponseException(
                $"Provider returned HTTP {statusCode} with no completion choices "
                + $"(likely upstream overload or content filter). Body preview: {preview}");
        }

        var sanitized = OpenAiResponseSanitizer.SanitizePayload(body);

        if (sanitized == body)
        {
            if (body.Contains("\":null", StringComparison.Ordinal))
                _logger?.LogDebug("Unsanitized null field in response: {Body}", Preview(body));

            return response;
        }

        response.Content = new StringContent(sanitized, Encoding.UTF8, "application/json");
        return response;
    }

    /// <summary>OpenRouter 等网关在 429/503 时通过 Retry-After 头给出建议等待秒数。</summary>
    private static TimeSpan? ReadRetryAfter(HttpResponseMessage response)
    {
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter is null)
            return null;

        var delay = retryAfter.Delta
            ?? (retryAfter.Date.HasValue ? retryAfter.Date.Value - DateTimeOffset.UtcNow : null);

        return delay is { } d && d > TimeSpan.Zero
            ? TimeSpan.FromSeconds(Math.Clamp(d.TotalSeconds, 1, 120))
            : null;
    }

    private static string Preview(string body) =>
        body.Length <= 512 ? body : string.Concat(body.AsSpan(0, 512), "…(truncated)");
}
