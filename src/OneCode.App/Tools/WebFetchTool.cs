using System.ComponentModel;
using System.Net;
using System.Text;
using AngleSharp.Html.Parser;

namespace OneCode.App.Tools;

/// <summary>
/// WebFetch tool - fetches content from a URL and applies a prompt to extract information.
/// </summary>
public sealed partial class WebFetchTool
{
    private static readonly HtmlParser _htmlParser = new();

    // Constants matching TypeScript implementation
    private const int MaxUrlLength = 2000;
    private const int MaxHttpContentLength = 10 * 1024 * 1024; // 10MB
    private const int FetchTimeoutMs = 60000;
    private const int MaxRedirects = 10;
    public const int MaxMarkdownLength = 100000;
    private const int CacheTtlMs = 15 * 60 * 1000; // 15 minutes

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly WebFetchCache _cache;
    private readonly ILogger<WebFetchTool> _logger;
    private readonly IBrowserPageRenderer? _browserRenderer;

    public WebFetchTool(
        IHttpClientFactory httpClientFactory,
        WebFetchCache cache,
        ILogger<WebFetchTool> logger,
        IBrowserPageRenderer? browserRenderer = null)
    {
        _httpClientFactory = httpClientFactory;
        _cache = cache;
        _logger = logger;
        _browserRenderer = browserRenderer;
    }

    private HttpClient CreateFetchHttpClient()
    {
        var client = _httpClientFactory.CreateClient("WebFetch");
        client.Timeout = TimeSpan.FromMilliseconds(FetchTimeoutMs);
        client.MaxResponseContentBufferSize = MaxHttpContentLength;
        return client;
    }

    [Description("Fetch content from a URL and return it as markdown, then apply a prompt to extract specific information. " +
                 "Use this to read web pages, documentation, or API responses. HTML is converted to markdown (headings, links, lists, code blocks preserved); other content types are returned as-is. " +
                 "SSRF protection: localhost, private IPs (10.x, 172.16-31.x, 192.168.x, 169.254.x), .internal/.local/.localhost hostnames, and IPv6 loopback/link-local are hard-blocked. " +
                 "Cross-host redirects return a redirect notice instead of following automatically — call WebFetch again with the redirected URL. " +
                 "JavaScript-rendered pages: when Playwright MCP is connected, WebFetch falls back to browser_navigate + browser_snapshot; otherwise the HTTP HTML→Markdown result is returned. " +
                 "Caching: successful fetches are cached for 15 minutes (max 50MB total); identical URLs return cached content within TTL. " +
                 "Size limits: max URL length 2000 chars, max response 10MB, max markdown output 100,000 chars (truncated with notice). " +
                 "HTTP is automatically upgraded to HTTPS.")]
    public async Task<ToolResult> FetchAsync(
        [Description("The URL to fetch. Must be http(s) and not point to a private/loopback host. Max 2000 chars. http:// is auto-upgraded to https://.")] string url,
        [Description("The prompt describing what information to extract from the fetched content. The fetched markdown is returned alongside this prompt so the caller can apply it. " +
                     "Example: 'extract the API endpoint and required headers'.")] string prompt,
        CancellationToken ct = default)
    {
        var start = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        if (!ValidateUrl(url))
        {
            return ToolResult.Error($"Invalid URL: {url}");
        }

        if (_cache.TryGet(url, out var cachedContent))
        {
            return ApplyPromptAndReturn(cachedContent, prompt, start);
        }

        try
        {
            var upgradedUrl = url;
            if (Uri.TryCreate(url, UriKind.Absolute, out var parsedUrl) && parsedUrl.Scheme == "http")
            {
                var builder = new UriBuilder(parsedUrl) { Scheme = "https" };
                upgradedUrl = builder.Uri.ToString();
            }

            var fetchResult = await FetchWithRedirects(upgradedUrl, url, 0, ct).ConfigureAwait(false);

            if (fetchResult.IsRedirect)
            {
                return ToolResult.JsonSuccess(new
                {
                    bytes = 0,
                    code = fetchResult.StatusCode,
                    codeText = GetStatusText(fetchResult.StatusCode),
                    result = $"REDIRECT DETECTED: The URL redirects to a different host.\n\n" +
                             $"Original URL: {fetchResult.OriginalUrl}\n" +
                             $"Redirect URL: {fetchResult.RedirectUrl}\n" +
                             $"Status: {fetchResult.StatusCode} {GetStatusText(fetchResult.StatusCode)}\n\n" +
                             $"To complete your request, I need to fetch content from the redirected URL. " +
                             $"Please use WebFetch again with these parameters:\n" +
                             $"- url: \"{fetchResult.RedirectUrl}\"\n" +
                             $"- prompt: \"{prompt}\"",
                    durationMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - start,
                    url
                });
            }

            var content = fetchResult.Content ?? "";
            var contentType = fetchResult.ContentType ?? "";
            var bytes = Encoding.UTF8.GetByteCount(content);

            string markdownContent;
            if (contentType.Contains("text/html", StringComparison.OrdinalIgnoreCase))
            {
                markdownContent = HtmlToMarkdown(content);
            }
            else
            {
                markdownContent = content;
            }

            if (string.IsNullOrWhiteSpace(markdownContent) || NeedsJsRendering(markdownContent))
            {
                var rendered = await TryBrowserRenderAsync(upgradedUrl, ct).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(rendered))
                    markdownContent = rendered;
            }

            _cache.Set(url, markdownContent, TimeSpan.FromMilliseconds(CacheTtlMs));

            return ApplyPromptAndReturn(markdownContent, prompt, start);
        }
        catch (OperationCanceledException)
        {
            return ToolResult.Error("Request was cancelled");
        }
        catch (Exception ex)
        {
            return ToolResult.Error(ex.Message);
        }
    }

    private async Task<FetchResult> FetchWithRedirects(
        string url,
        string originalUrl,
        int depth,
        CancellationToken ct)
    {
        if (depth > MaxRedirects)
        {
            throw new InvalidOperationException($"Too many redirects (exceeded {MaxRedirects})");
        }

        try
        {
            using var client = CreateFetchHttpClient();
            var response = await client.GetAsync(url, ct).ConfigureAwait(false);

            // 301/302/303/307/308 均按重定向处理。
            // 注：HttpStatusCode.Moved 与 MovedPermanently 同为 301（不重复列出）；
            // 308 PermanentRedirect 在旧版 .NET 枚举中缺失，故保留显式转换。
            if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or
                HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or
                (HttpStatusCode)308)
            {
                var location = response.Headers.Location?.ToString();
                if (string.IsNullOrEmpty(location))
                {
                    throw new InvalidOperationException("Redirect missing Location header");
                }

                if (Uri.TryCreate(location, UriKind.Relative, out var relativeUri))
                {
                    var baseUri = new Uri(url);
                    location = new Uri(baseUri, relativeUri).ToString();
                }

                if (IsPermittedRedirect(originalUrl, location))
                {
                    return await FetchWithRedirects(location, originalUrl, depth + 1, ct).ConfigureAwait(false);
                }

                return new FetchResult
                {
                    IsRedirect = true,
                    OriginalUrl = originalUrl,
                    RedirectUrl = location,
                    StatusCode = (int)response.StatusCode,
                };
            }

            response.EnsureSuccessStatusCode();

            var content = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var contentType = response.Content.Headers.ContentType?.MediaType ?? "";

            return new FetchResult
            {
                Content = content,
                ContentType = contentType,
                StatusCode = (int)response.StatusCode,
            };
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.Forbidden)
        {
            throw new InvalidOperationException($"Access to {url} is blocked", ex);
        }
    }


    private static ToolResult ApplyPromptAndReturn(string content, string prompt, long startMs)
    {
        var truncatedContent = content.Length > MaxMarkdownLength
            ? content[..MaxMarkdownLength] + "\n\n[Content truncated due to length...]"
            : content;

        var result = JsonSerializer.Serialize(new
        {
            bytes = Encoding.UTF8.GetByteCount(content),
            code = 200,
            codeText = "OK",
            result = $"Content from URL (prompt: {prompt}):\n\n{truncatedContent}",
            durationMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - startMs,
        });

        return ToolResult.Success(result);
    }

    private static string GetStatusText(int statusCode) => statusCode switch
    {
        301 => "Moved Permanently",
        302 => "Found",
        307 => "Temporary Redirect",
        308 => "Permanent Redirect",
        _ => "Redirect"
    };

    private static bool NeedsJsRendering(string content)
    {
        if (content.Length < 50) return true;

        var jsIndicators = new[] { "You need to enable JavaScript", "requires JavaScript", "Please enable JavaScript", "<noscript>" };
        foreach (var indicator in jsIndicators)
        {
            if (content.Contains(indicator, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private async Task<string?> TryBrowserRenderAsync(string url, CancellationToken ct)
    {
        if (_browserRenderer is null)
            return null;

        try
        {
            return await _browserRenderer.RenderAsync(url, timeoutMs: 30_000, ct: ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Browser render fallback failed for {Url}", url);
            return null;
        }
    }

    private sealed class FetchResult
    {
        public bool IsRedirect { get; init; }
        public string? Content { get; init; }
        public string? ContentType { get; init; }
        public int StatusCode { get; init; }
        public string? OriginalUrl { get; init; }
        public string? RedirectUrl { get; init; }
    }
}
