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
