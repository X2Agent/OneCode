namespace OneCode.Infrastructure.Ai;

/// <summary>
/// Sanitizes JSON and SSE responses from OpenAI-compatible APIs before the official
/// OpenAI .NET SDK deserializes them. Common vendor mismatches:
/// <list type="bullet">
/// <item><c>tool_calls</c> / <c>annotations</c> sent as <c>null</c> instead of <c>[]</c></item>
/// <item><c>finish_reason</c> sent as <c>""</c> or a vendor alias instead of an official enum value</item>
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

        // 200 + 显式错误体（{"error":{...}}）：部分网关（OpenRouter → Nvidia 等）过载时
        // 不返回 4xx/5xx 而是 200 + 错误体。提取上游真实错误消息与错误码，
        // 用专用异常承载，避免被笼统归类为「empty choices」误导排查。
        // 是否可重试由异常依据错误码/消息自行判定（瞬时错误自动重试，4xx 立即失败）。
        if (OpenAiResponseSanitizer.TryExtractUpstreamError(body, out var upstreamMessage, out var upstreamCode))
        {
            const int MaxLoggedBodyLength = 512;
            var preview = body.Length <= MaxLoggedBodyLength
                ? body
                : string.Concat(body.AsSpan(0, MaxLoggedBodyLength), "…(truncated)");
            _logger?.LogWarning(
                "Provider returned HTTP {Status} with an upstream error body — treating as {Kind}. Body: {Body}",
                (int)response.StatusCode,
                UpstreamProviderErrorException.ClassifyTransient(upstreamCode, upstreamMessage) ? "transient (retryable)" : "permanent (fail fast)",
                preview);
            throw new UpstreamProviderErrorException(
                $"Provider returned HTTP {(int)response.StatusCode} with an upstream error body: \"{upstreamMessage}\""
                + (upstreamCode is null ? "" : $" (code: {upstreamCode})"),
                upstreamCode);
        }

        // 200 + 空 choices：OpenRouter 等供应商在上游过载/内容过滤时返回这种"成功"响应，
        // 官方 SDK 反序列化时会在 ChatCompletion.get_Role() 内部抛 ArgumentOutOfRangeException。
        // 转为专用异常，交由 RetryOnOverloadChatClient 按瞬时上游错误重试。
        if (OpenAiResponseSanitizer.HasEmptyChoices(body))
        {
            const int MaxLoggedBodyLength = 512;
            var preview = body.Length <= MaxLoggedBodyLength
                ? body
                : string.Concat(body.AsSpan(0, MaxLoggedBodyLength), "…(truncated)");
            _logger?.LogWarning(
                "Provider returned HTTP {Status} with empty choices — treating as transient upstream error. Body: {Body}",
                (int)response.StatusCode, preview);
            throw new EmptyChoicesResponseException(
                $"Provider returned HTTP {(int)response.StatusCode} with no completion choices "
                + $"(likely upstream overload or content filter). Body preview: {preview}");
        }

        var sanitized = OpenAiResponseSanitizer.SanitizePayload(body);

        if (sanitized == body)
        {
            // Diagnostic: a remaining null field was not rewritten. Helps locate missed array fields.
            // Debug level + truncated preview: the full body may be large and contain conversation content.
            if (body.Contains("\":null", StringComparison.Ordinal))
            {
                const int MaxLoggedBodyLength = 512;
                var preview = body.Length <= MaxLoggedBodyLength
                    ? body
                    : string.Concat(body.AsSpan(0, MaxLoggedBodyLength), "…(truncated)");
                _logger?.LogDebug("Unsanitized null field in response: {Body}", preview);
            }

            return response;
        }

        response.Content = new StringContent(sanitized, Encoding.UTF8, "application/json");
        return response;
    }
}
