namespace OneCode.Infrastructure.Ai;

/// <summary>
/// Sanitizes JSON responses from OpenAI-compatible APIs that return null
/// for array fields (e.g. tool_calls, annotations) instead of an empty array.
/// The OpenAI .NET SDK calls EnumerateArray() on these fields, which throws
/// InvalidOperationException when the element is Null.
/// </summary>
public sealed partial class OpenAiResponseSanitizingHandler : DelegatingHandler
{
    // Matches "tool_calls": null or "annotations": null (with any whitespace).
    // These are the fields most commonly returned as null by third-party
    // OpenAI-compatible providers (DeepSeek, Qwen, Moonshot, etc.)
    // where the OpenAI SDK expects an array.
    [GeneratedRegex(@"""(tool_calls|annotations)""\s*:\s*null\b")]
    private static partial Regex NullArrayRegex();

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

        var mediaType = response.Content?.Headers?.ContentType?.MediaType;
        if (mediaType != "application/json")
            return response;

        var body = await response.Content!.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var sanitized = NullArrayRegex().Replace(body, @"""$1"":[]");

        if (sanitized == body)
        {
            // 诊断日志：响应中仍包含 null 字段但未被正则命中，帮助定位遗漏的数组字段
            if (body.Contains("\":null"))
            {
                _logger?.LogWarning("Unsanitized null field in response: {Body}", body);
            }
            return response;
        }

        response.Content = new StringContent(sanitized, Encoding.UTF8, "application/json");
        return response;
    }
}
