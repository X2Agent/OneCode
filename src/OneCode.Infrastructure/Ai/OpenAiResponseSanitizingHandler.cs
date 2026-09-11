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
        var statusCode = (int)response.StatusCode;

        // 网关过载时常返回 200/400 + 错误体，并把上游真实错误掩码为不透明码
        // （如 "openai_error" / "bad_response_status_code"）。瞬时性由异常判定：
        // 网关中继类默认瞬时（自动重试），仅永久性证据（无效密钥/配额等）快速失败。
        if (OpenAiResponseSanitizer.TryExtractUpstreamError(body, out var upstreamMessage, out var upstreamCode))
        {
            var preview = Preview(body);
            _logger?.LogWarning(
                "Provider returned HTTP {Status} with an upstream error body — treating as {Kind}. Body: {Body}",
                statusCode,
                UpstreamProviderErrorException.ClassifyTransient(upstreamCode, upstreamMessage, statusCode)
                    ? "transient (retryable)"
                    : "permanent (fail fast)",
                preview);
            throw new UpstreamProviderErrorException(
                $"Provider returned HTTP {statusCode} with an upstream error body: \"{upstreamMessage}\""
                + (upstreamCode is null ? "" : $" (code: {upstreamCode})"),
                upstreamCode,
                statusCode);
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

    private static string Preview(string body) =>
        body.Length <= 512 ? body : string.Concat(body.AsSpan(0, 512), "…(truncated)");
}
