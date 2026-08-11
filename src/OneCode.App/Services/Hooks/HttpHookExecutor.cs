using System.Net.Http.Headers;
using System.Text;

namespace OneCode.App.Services.Hooks;

/// <summary>
/// HTTP 类型 Hook 执行器——通过 IHttpClientFactory 发起通用 HTTP 调用。
///
/// 模板插值：URL / Headers 值 / Body 均支持 {{Field}} 语法替换 HookPayload 字段，
/// 由 <see cref="HookTemplateRenderer"/> 统一实现，与 NotificationHookExecutor 共享同一插值语义。
///
/// 与 Notification 的区别：HTTP 面向通用 HTTP 调用（自定义 URL/Method/Headers/Body），
/// Notification 面向消息推送业务场景（飞书/企微等固定渠道格式）。
/// </summary>
public sealed class HttpHookExecutor : IHookExecutor
{
    private const string HttpClientName = "HookHttp";
    private const string DefaultMethod = "POST";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<HttpHookExecutor> _logger;

    public HttpHookExecutor(
        IHttpClientFactory httpClientFactory,
        ILogger<HttpHookExecutor> logger)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public HookType Type => HookType.Http;

    public async Task<HookResult?> ExecuteAsync(
        HookPayload payload, HookConfig config, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(config.Url))
        {
            _logger.LogWarning("HTTP hook has no url specified");
            return new HookResult
            {
                Outcome = HookOutcome.NonBlockingError,
                Message = "HTTP hook missing 'url' field",
            };
        }

        var method = string.IsNullOrWhiteSpace(config.Method)
            ? DefaultMethod
            : config.Method!.ToUpperInvariant();
        var renderedUrl = HookTemplateRenderer.Render(config.Url, payload);

        if (!Uri.TryCreate(renderedUrl, UriKind.Absolute, out var uri))
        {
            _logger.LogWarning("HTTP hook url is invalid: {Url}", renderedUrl);
            return new HookResult
            {
                Outcome = HookOutcome.NonBlockingError,
                Message = $"HTTP hook invalid url: {renderedUrl}",
            };
        }

        var timeoutMs = config.TimeoutMs ?? 5000;
        using var timeoutCts = new CancellationTokenSource(timeoutMs);
        try
        {
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

            using var request = new HttpRequestMessage(new HttpMethod(method), uri);

            if (config.Headers is { Count: > 0 })
            {
                foreach (var (key, value) in config.Headers)
                {
                    if (string.IsNullOrWhiteSpace(key)) continue;
                    request.Headers.TryAddWithoutValidation(key, HookTemplateRenderer.Render(value, payload));
                }
            }

            if (!string.IsNullOrEmpty(config.Body) && HasBody(method))
            {
                var renderedBody = HookTemplateRenderer.Render(config.Body, payload);
                request.Content = new StringContent(renderedBody, Encoding.UTF8);
                if (request.Headers.Accept.Count == 0)
                {
                    request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                }
            }

            var client = _httpClientFactory.CreateClient(HttpClientName);
            using var response = await client.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, linkedCts.Token)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(linkedCts.Token)
                    .ConfigureAwait(false);
                return new HookResult
                {
                    Outcome = HookOutcome.NonBlockingError,
                    Message = $"HTTP {method} {renderedUrl} returned {(int)response.StatusCode} {response.ReasonPhrase}: {Truncate(errorBody, 500)}",
                };
            }

            return null;
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            _logger.LogWarning("HTTP hook timed out after {TimeoutMs}ms: {Method} {Url}",
                timeoutMs, method, renderedUrl);
            return new HookResult
            {
                Outcome = HookOutcome.NonBlockingError,
                Message = $"HTTP hook timed out after {timeoutMs}ms",
            };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "HTTP hook execution failed: {Method} {Url}", method, renderedUrl);
            return new HookResult
            {
                Outcome = HookOutcome.NonBlockingError,
                Message = $"HTTP hook error: {ex.Message}",
            };
        }
    }

    private static bool HasBody(string method) =>
        method is "POST" or "PUT" or "PATCH" or "DELETE";

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength] + "...";
}
