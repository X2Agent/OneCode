using System.ComponentModel;
using System.Net;
using System.Text;
using AngleSharp.Dom;
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
    private readonly PlaywrightRenderer _playwrightRenderer;
    private readonly WebFetchCache _cache;
    private readonly ILogger<WebFetchTool> _logger;

    public WebFetchTool(
        IHttpClientFactory httpClientFactory,
        PlaywrightRenderer playwrightRenderer,
        WebFetchCache cache,
        ILogger<WebFetchTool> logger)
    {
        _httpClientFactory = httpClientFactory;
        _playwrightRenderer = playwrightRenderer;
        _cache = cache;
        _logger = logger;
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
                 "JavaScript-rendered pages: if the initial fetch indicates JS is required, Playwright is used as a fallback renderer (30s timeout). " +
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
                var playwrightContent = await TryPlaywrightRenderAsync(upgradedUrl, ct).ConfigureAwait(false);
                if (playwrightContent != null)
                    markdownContent = playwrightContent;
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

    private static bool IsPermittedRedirect(string originalUrl, string redirectUrl)
    {
        if (!Uri.TryCreate(originalUrl, UriKind.Absolute, out var original) ||
            !Uri.TryCreate(redirectUrl, UriKind.Absolute, out var redirect))
        {
            return false;
        }

        if (original.Scheme != redirect.Scheme) return false;
        if (original.Port != redirect.Port) return false;
        if (!string.IsNullOrEmpty(redirect.UserInfo)) return false;

        // Allow www. variations
        var stripWww = new Func<string, string>(h => h.StartsWith("www.", StringComparison.Ordinal) ? h[4..] : h);
        return stripWww(original.Host) == stripWww(redirect.Host);
    }

    private static bool ValidateUrl(string url)
    {
        if (url.Length > MaxUrlLength) return false;

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;

        if (uri.Scheme != "http" && uri.Scheme != "https") return false;

        // Block URLs with credentials
        if (!string.IsNullOrEmpty(uri.UserInfo)) return false;

        var host = uri.Host;
        if (string.IsNullOrEmpty(host)) return false;

        // Block localhost by name
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return false;

        // Block hostname patterns commonly used for internal services
        if (host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".local", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase))
            return false;

        // Try to parse host as IP address (covers both IPv4 and IPv6)
        if (IPAddress.TryParse(host, out var ipAddress))
        {
            // Block any private/loopback/link-local IP address
            if (IsPrivateOrLocalAddress(ipAddress))
                return false;
        }
        else
        {
            // Hostname (not an IP) — block known private hostname prefixes
            var parts = host.Split('.');
            if (parts.Length < 2) return false;

            // Block private IPv4 ranges by prefix (covers cases where DNS resolves to dotted-decimal hostname)
            if (host.StartsWith("192.168.", StringComparison.OrdinalIgnoreCase) ||
                host.StartsWith("10.", StringComparison.OrdinalIgnoreCase))
                return false;

            // Block 172.16.0.0 – 172.31.255.255 precisely (not all 172.x)
            if (host.StartsWith("172.", StringComparison.OrdinalIgnoreCase) && parts.Length >= 2)
            {
                if (int.TryParse(parts[1], out var secondOctet) && secondOctet >= 16 && secondOctet <= 31)
                    return false;
            }

            // Block 169.254.x.x (link-local / cloud metadata service)
            if (host.StartsWith("169.254.", StringComparison.OrdinalIgnoreCase))
                return false;

            // Block 0.0.0.0
            if (host == "0.0.0.0")
                return false;
        }

        return true;
    }

    /// <summary>
    /// Determines whether an IP address is private, loopback, link-local, or otherwise
    /// unsafe for outbound fetches. Covers IPv4 and IPv6 ranges to prevent SSRF attacks.
    /// Exposed as internal for use by the SocketsHttpHandler ConnectCallback (DNS rebinding protection).
    /// </summary>
    internal static bool IsPrivateOrLocalAddressPublic(IPAddress address)
    {
        return IsPrivateOrLocalAddress(address);
    }

    private static bool IsPrivateOrLocalAddress(IPAddress address)
    {
        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            // IPv4
            var bytes = address.GetAddressBytes();
            if (bytes.Length != 4) return true; // defensive: treat malformed as private

            // 0.0.0.0/8 — "this network"
            if (bytes[0] == 0) return true;
            // 10.0.0.0/8 — private
            if (bytes[0] == 10) return true;
            // 127.0.0.0/8 — loopback
            if (bytes[0] == 127) return true;
            // 169.254.0.0/16 — link-local (cloud metadata service)
            if (bytes[0] == 169 && bytes[1] == 254) return true;
            // 172.16.0.0/12 — private
            if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) return true;
            // 192.168.0.0/16 — private
            if (bytes[0] == 192 && bytes[1] == 168) return true;
            // 100.64.0.0/10 — CGNAT (shared address space, treat as private)
            if (bytes[0] == 100 && bytes[1] >= 64 && bytes[1] <= 127) return true;

            return false;
        }

        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            // IPv6
            // ::1 — loopback
            if (IPAddress.IsLoopback(address)) return true;
            if (address.IsIPv6LinkLocal) return true;   // fe80::/10
            if (address.IsIPv6SiteLocal) return true;   // fec0::/10 (deprecated but still block)
            if (address.IsIPv6UniqueLocal) return true; // fc00::/7

            // :: (unspecified address)
            if (address.Equals(IPAddress.IPv6None)) return true;

            // IPv4-mapped IPv6 addresses (::ffff:a.b.c.d) — check the embedded IPv4
            if (address.IsIPv4MappedToIPv6)
            {
                var mapped = address.MapToIPv4();
                return IsPrivateOrLocalAddress(mapped);
            }

            return false;
        }

        // Unknown address family — treat as private for safety
        return true;
    }

    /// <summary>
    /// HTML→Markdown 转换器——基于 AngleSharp DOM 遍历，替代正则解析。
    ///
    /// <para>设计要点：</para>
    /// <list type="bullet">
    ///   <item>用 AngleSharp 解析 HTML 为 DOM 树，彻底消除正则解析 HTML 的 ReDoS 风险和正确性问题</item>
    ///   <item>递归遍历 DOM 节点，按标签类型映射为 Markdown 语法</item>
    ///   <item>正确处理嵌套标签（如 &lt;strong&gt;&lt;em&gt;text&lt;/em&gt;&lt;/strong&gt; → <c>***text***</c>）</item>
    ///   <item>自动移除 &lt;script&gt; / &lt;style&gt; 标签内容</item>
    ///   <item>HTML 实体（&amp;amp;、&amp;nbsp; 等）由 AngleSharp 自动解码</item>
    /// </list>
    ///
    /// <para>转换规则与原正则实现保持一致（h1-h6/p/a/strong/em/b/i/li/pre/code/br/hr），
    /// 确保调用方输出格式不变。</para>
    /// </summary>
    private static string HtmlToMarkdown(string html)
    {
        if (string.IsNullOrWhiteSpace(html))
            return string.Empty;

        // 解析 HTML——AngleSharp 容错性强，能处理畸形 HTML 而不会 ReDoS
        var document = _htmlParser.ParseDocument(html);

        // 移除 script / style 标签（含内容）
        foreach (var element in document.QuerySelectorAll("script, style").ToList())
            element.Remove();

        // 遍历 body 子节点，转换为 Markdown
        var sb = new StringBuilder(html.Length);
        if (document.Body is not null)
        {
            foreach (var node in document.Body.ChildNodes)
                RenderNode(sb, node, inPreBlock: false);
        }

        // 规范化空白：多个连续空格合并为一个，超过 2 个换行压缩为 2 个
        return NormalizeWhitespace(sb.ToString());
    }

    /// <summary>递归渲染单个 DOM 节点为 Markdown 文本。</summary>
    /// <param name="inPreBlock">是否在 pre/code 块内（保留原始文本，不做实体解码之外的加工）。</param>
    private static void RenderNode(StringBuilder sb, INode node, bool inPreBlock)
    {
        switch (node)
        {
            case IText textNode:
                // AngleSharp 的 TextContent 已自动解码 HTML 实体（&amp; → & 等）
                sb.Append(textNode.Text);
                break;

            case IElement element:
                RenderElement(sb, element, inPreBlock);
                break;

            case IComment:
                // 忽略 HTML 注释
                break;
        }
    }

    /// <summary>
    /// 渲染一个 HTML 元素为 Markdown。
    /// </summary>
    private static void RenderElement(StringBuilder sb, IElement element, bool inPreBlock)
    {
        var tagName = element.TagName.ToLowerInvariant();

        switch (tagName)
        {
            // 标题 h1-h6
            case "h1":
            case "h2":
            case "h3":
            case "h4":
            case "h5":
            case "h6":
                var level = int.Parse(tagName[1..], CultureInfo.InvariantCulture);
                sb.Append(new string('#', level)).Append(' ');
                RenderChildren(sb, element, inPreBlock);
                sb.Append("\n\n");
                break;

            // 段落
            case "p":
                RenderChildren(sb, element, inPreBlock);
                sb.Append("\n\n");
                break;

            // 链接
            case "a":
                var href = element.GetAttribute("href");
                var linkText = element.TextContent?.Trim() ?? string.Empty;
                if (!string.IsNullOrEmpty(href) && !string.IsNullOrEmpty(linkText))
                    sb.Append(CultureInfo.InvariantCulture, $"[{linkText}]({href})");
                else
                    sb.Append(linkText);
                break;

            // 加粗
            case "strong":
            case "b":
                sb.Append("**");
                RenderChildren(sb, element, inPreBlock);
                sb.Append("**");
                break;

            // 斜体
            case "em":
            case "i":
                sb.Append('*');
                RenderChildren(sb, element, inPreBlock);
                sb.Append('*');
                break;

            // 列表项
            case "li":
                sb.Append("- ");
                RenderChildren(sb, element, inPreBlock);
                sb.Append('\n');
                break;

            // ul / ol（列表容器）——仅渲染子节点，列表项自己处理格式
            case "ul":
            case "ol":
                RenderChildren(sb, element, inPreBlock);
                sb.Append('\n');
                break;

            // 预格式化代码块：pre 内的 code 标签内容原样输出
            case "pre":
                var codeChild = element.QuerySelector("code");
                var preContent = codeChild?.TextContent ?? element.TextContent;
                sb.Append("```\n").Append(preContent.TrimEnd('\n', '\r')).Append("\n```\n");
                break;

            // 行内代码
            case "code":
                sb.Append('`');
                sb.Append(element.TextContent);
                sb.Append('`');
                break;

            // 换行
            case "br":
                sb.Append('\n');
                break;

            // 水平分割线
            case "hr":
                sb.Append("\n---\n");
                break;

            // div / span / section / article 等容器：递归渲染子节点
            case "div":
            case "span":
            case "section":
            case "article":
            case "header":
            case "footer":
            case "main":
            case "nav":
            case "aside":
            case "blockquote":
                RenderChildren(sb, element, inPreBlock);
                break;

            // 其他标签：渲染子节点（保持内容，丢弃标签）
            default:
                RenderChildren(sb, element, inPreBlock);
                break;
        }
    }

    /// <summary>递归渲染元素的所有子节点。</summary>
    private static void RenderChildren(StringBuilder sb, IElement element, bool inPreBlock)
    {
        foreach (var child in element.ChildNodes)
            RenderNode(sb, child, inPreBlock);
    }

    /// <summary>
    /// 规范化空白：合并连续空格/制表符为单个空格（不跨行），压缩 3+ 连续换行为 2 个，去除首尾空白。
    /// </summary>
    private static string NormalizeWhitespace(string text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        var sb = new StringBuilder(text.Length);
        var i = 0;
        var length = text.Length;

        while (i < length)
        {
            var ch = text[i];

            // 连续空格/制表符合并为单个空格（不跨行）
            if (ch == ' ' || ch == '\t')
            {
                sb.Append(' ');
                while (i < length && (text[i] == ' ' || text[i] == '\t'))
                    i++;
                continue;
            }

            // 连续换行（\n / \r）压缩为最多 2 个
            if (ch == '\n' || ch == '\r')
            {
                // 跳过所有连续的换行符
                while (i < length && (text[i] == '\n' || text[i] == '\r'))
                    i++;
                sb.Append("\n\n");
                continue;
            }

            sb.Append(ch);
            i++;
        }

        return sb.ToString().Trim();
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

    private async Task<string?> TryPlaywrightRenderAsync(string url, CancellationToken ct)
    {
        try
        {
            return await _playwrightRenderer.RenderAsync(url, timeoutMs: 30000, ct: ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // PlaywrightRenderer already logs its internal failures at Debug/Error level.
            // This outer catch guards against unexpected exceptions escaping RenderAsync
            // (e.g. ObjectDisposedException if the renderer was disposed concurrently).
            _logger.LogDebug(ex, "TryPlaywrightRenderAsync failed for {Url}", url);
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
