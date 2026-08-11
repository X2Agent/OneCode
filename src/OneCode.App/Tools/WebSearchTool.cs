using OneCode.Infrastructure.Config;
using System.ComponentModel;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using CoreConstants = OneCode.Core.Constants;

namespace OneCode.App.Tools;

/// <summary>
/// WebSearch tool — performs web searches using a configured provider.
/// Supports Brave Search API and DuckDuckGo HTML fallback.
/// </summary>
public sealed partial class WebSearchTool
{
    private const int MaxResults = 8;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfigManager _config;

    // 单例复用 HtmlParser——DuckDuckGo HTML 解析专用。
    // AngleSharp 的 HtmlParser 是线程安全的（每次 ParseDocument 返回独立 IDocument），可安全共享。
    private static readonly HtmlParser _htmlParser = new();

    public WebSearchTool(IConfigManager config, IHttpClientFactory httpClientFactory)
    {
        _config = config;
        _httpClientFactory = httpClientFactory;
    }

    private HttpClient CreateSearchHttpClient()
    {
        var client = _httpClientFactory.CreateClient(Constants.HttpClientNames.WebSearch);
        client.Timeout = TimeSpan.FromSeconds(Constants.Timeouts.WebSearch);
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("OneCode", "1.0"));
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json, text/html;q=0.9, */*;q=0.1");
        return client;
    }

    [Description("Search the web for current information, returning a list of results with title, URL, and snippet. " +
                 "Use this to find up-to-date information beyond your knowledge cutoff (e.g. latest library versions, recent API changes, current events). " +
                 "Provider: configured via the OneCode_WebSearchProvider setting or environment variable. 'Brave' uses the Brave Search API (requires API key); 'DuckDuckGo' (default) uses the HTML endpoint with no key required. " +
                 "Domain filtering: allowed_domains restricts results to the listed domains (whitelist); blocked_domains excludes them (blacklist). Both accept bare hostnames (www. prefix is stripped automatically). " +
                 "Result limit: maximum 8 results per call. " +
                 "Query length: must be at least 2 characters. " +
                 "Tip: for reading a specific known URL, use WebFetch instead; for general research, use WebSearch first then WebFetch on the most relevant results.")]
    public async Task<ToolResult> SearchAsync(
        [Description("The search query. Must be at least 2 characters. Use specific terms for better results; avoid overly broad queries like 'javascript'.")] string query,
        [Description("Whitelist: only include results from these domains. Example: ['docs.microsoft.com', 'github.com']. www. is stripped automatically. Omit for no whitelist.")] string[]? allowed_domains = null,
        [Description("Blacklist: exclude results from these domains. Example: ['w3schools.com', 'pinterest.com']. www. is stripped automatically. Omit for no blacklist.")] string[]? blocked_domains = null,
        CancellationToken ct = default)
    {
        if (query.Length < 2)
            return ToolResult.Error("query must be at least 2 characters");

        var settings = _config.Current.Effective;
        var provider = ResolveProvider(settings);

        var startTime = DateTimeOffset.UtcNow;

        try
        {
            var results = provider switch
            {
                WebSearchProvider.Brave => await SearchBraveAsync(query, allowed_domains, blocked_domains, settings, ct).ConfigureAwait(false),
                _ => await SearchDuckDuckGoAsync(query, allowed_domains, blocked_domains, ct).ConfigureAwait(false),
            };
            var durationSeconds = (DateTimeOffset.UtcNow - startTime).TotalSeconds;
            return ToolResult.JsonSuccess(new
            {
                provider = provider.ToString().ToLowerInvariant(),
                query,
                results = results.Select(h => new { title = h.Title, url = h.Url, snippet = h.Snippet }).ToList(),
                durationSeconds,
            });
        }
        catch (Exception ex)
        {
            return ToolResult.Error($"WebSearch error: {ex.Message}");
        }
    }

    private static WebSearchProvider ResolveProvider(AppSettings settings)
    {
        var providerValue = settings.WebSearchProvider;

        return Enum.TryParse<WebSearchProvider>(providerValue, ignoreCase: true, out var provider)
            ? provider
            : WebSearchProvider.DuckDuckGo;
    }

    private async Task<IReadOnlyList<SearchHit>> SearchBraveAsync(
        string query,
        string[]? allowedDomains,
        string[]? blockedDomains,
        AppSettings settings,
        CancellationToken ct)
    {
        var apiKey = Environment.GetEnvironmentVariable(CoreConstants.EnvVars.BraveSearchApiKey)
            ?? settings.WebSearchApiKey;

        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("Brave search requires BRAVE_SEARCH_API_KEY or webSearchApiKey configuration.");

        var url = $"https://api.search.brave.com/res/v1/web/search?q={Uri.EscapeDataString(query)}&count={MaxResults}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("X-Subscription-Token", apiKey);

        using var httpClient = CreateSearchHttpClient();
        using var response = await httpClient.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var responseStream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var json = await JsonDocument.ParseAsync(responseStream, cancellationToken: ct).ConfigureAwait(false);

        List<SearchHit> hits = [];
        if (json.RootElement.TryGetProperty("web", out var web)
            && web.TryGetProperty("results", out var results)
            && results.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in results.EnumerateArray())
            {
                var title = item.TryGetProperty("title", out var titleEl) ? titleEl.GetString() : null;
                var urlValue = item.TryGetProperty("url", out var urlEl) ? urlEl.GetString() : null;
                var snippet = item.TryGetProperty("description", out var descEl) ? descEl.GetString() : null;

                if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(urlValue))
                    continue;

                hits.Add(new SearchHit(title, urlValue, snippet));
            }
        }

        return FilterDomains(hits, allowedDomains, blockedDomains);
    }

    private async Task<IReadOnlyList<SearchHit>> SearchDuckDuckGoAsync(
        string query,
        string[]? allowedDomains,
        string[]? blockedDomains,
        CancellationToken ct)
    {
        var url = $"https://html.duckduckgo.com/html/?q={Uri.EscapeDataString(query)}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using var httpClient = CreateSearchHttpClient();
        using var response = await httpClient.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var html = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var hits = ParseDuckDuckGoResults(html);
        return FilterDomains(hits, allowedDomains, blockedDomains);
    }

    /// <summary>
    /// 解析 DuckDuckGo HTML 搜索结果页，提取链接、标题和摘要。
    ///
    /// <para>基于 AngleSharp DOM 遍历实现，替代原正则方案：</para>
    /// <list type="bullet">
    ///   <item>用 <c>QuerySelectorAll("a.result__a")</c> 精确定位结果链接</item>
    ///   <item>用 <see cref="FindNextSnippet"/> 在后续兄弟节点中查找对应的摘要</item>
    ///   <item>DOM 解析正确处理嵌套标签、HTML 实体、畸形 HTML，无 ReDoS 风险</item>
    /// </list>
    /// </summary>
    private static IReadOnlyList<SearchHit> ParseDuckDuckGoResults(string html)
    {
        if (string.IsNullOrWhiteSpace(html))
            return [];

        var document = _htmlParser.ParseDocument(html);
        var linkElements = document.QuerySelectorAll("a.result__a");

        List<SearchHit> hits = [];
        foreach (var linkEl in linkElements)
        {
            if (hits.Count >= MaxResults) break;

            var rawHref = linkEl.GetAttribute("href") ?? "";
            // AngleSharp 的 GetAttribute 已解码 HTML 实体，HtmlDecode 是防御性二二解码保护（对已解码文本无害）
            var title = NormalizeWhitespace(linkEl.TextContent ?? "");
            var resolvedUrl = ResolveDuckDuckGoRedirect(WebUtility.HtmlDecode(rawHref));

            if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(resolvedUrl))
                continue;

            // 在后续兄弟节点中查找最近的 result__snippet
            var snippetEl = FindNextSnippet(linkEl);
            var snippet = snippetEl is not null
                ? NormalizeWhitespace(snippetEl.TextContent ?? "")
                : null;
            if (string.IsNullOrEmpty(snippet))
                snippet = null;

            hits.Add(new SearchHit(title, resolvedUrl, snippet));
        }

        return hits;
    }

    /// <summary>
    /// 从当前元素开始，向后续兄弟节点查找最近的 <c>result__snippet</c> 元素。
    /// 遇到下一个 <c>result__a</c> 时停止（避免跨结果关联）。
    /// </summary>
    private static IElement? FindNextSnippet(IElement start)
    {
        var sibling = start.NextElementSibling;
        while (sibling is not null)
        {
            if (sibling.ClassList.Contains("result__snippet"))
                return sibling;

            // 遇到下一个结果链接——停止搜索，避免将后续结果的摘要误关联到当前链接
            if (sibling.TagName.Equals("A", StringComparison.OrdinalIgnoreCase) &&
                sibling.ClassList.Contains("result__a"))
                return null;

            sibling = sibling.NextElementSibling;
        }
        return null;
    }

    private static IReadOnlyList<SearchHit> FilterDomains(
        IEnumerable<SearchHit> hits,
        string[]? allowedDomains,
        string[]? blockedDomains)
    {
        var allowed = allowedDomains?.Where(s => !string.IsNullOrWhiteSpace(s)).Select(NormalizeDomain).ToArray() ?? [];
        var blocked = blockedDomains?.Where(s => !string.IsNullOrWhiteSpace(s)).Select(NormalizeDomain).ToArray() ?? [];

        return hits.Where(hit =>
        {
            if (!Uri.TryCreate(hit.Url, UriKind.Absolute, out var uri))
                return false;

            var host = NormalizeDomain(uri.Host);
            if (allowed.Length > 0 && !allowed.Any(domain => host == domain || host.EndsWith($".{domain}", StringComparison.OrdinalIgnoreCase)))
                return false;

            if (blocked.Any(domain => host == domain || host.EndsWith($".{domain}", StringComparison.OrdinalIgnoreCase)))
                return false;

            return true;
        }).Take(MaxResults).ToList();
    }

    private static string NormalizeDomain(string domain)
    {
        var trimmed = domain.Trim().Trim('.');
        return trimmed.StartsWith("www.", StringComparison.OrdinalIgnoreCase)
            ? trimmed[4..].ToLowerInvariant()
            : trimmed.ToLowerInvariant();
    }

    private static string ResolveDuckDuckGoRedirect(string href)
    {
        if (!Uri.TryCreate(href, UriKind.Absolute, out var uri))
            return href;

        if (!uri.Host.Contains("duckduckgo.com", StringComparison.OrdinalIgnoreCase))
            return href;

        var query = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in query)
        {
            var idx = part.IndexOf('=');
            if (idx <= 0)
                continue;

            var key = part[..idx];
            if (!string.Equals(key, "uddg", StringComparison.OrdinalIgnoreCase))
                continue;

            return Uri.UnescapeDataString(part[(idx + 1)..]);
        }

        return href;
    }

    /// <summary>
    /// 清理 HTML 文本：去除标签、解码 HTML 实体、压缩空白。
    ///
    /// <para>基于字符遍历实现，替代原正则方案（HtmlTagRegex + Regex.Replace("\s+"))：
    /// 消除重复编译开销和 ReDoS 隐患。</para>
    ///
    /// <para>处理步骤：</para>
    /// <list type="number">
    ///   <item>遍历字符，将 <c>&lt;...&gt;</c> 标签替换为单个空格</item>
    ///   <item>用 <see cref="WebUtility.HtmlDecode"/> 解码 HTML 实体（&amp;amp; → &amp; 等）</item>
    ///   <item>用 <see cref="NormalizeWhitespace"/> 压缩连续空白为单个空格</item>
    /// </list>
    /// </summary>
    private static string CleanHtmlText(string text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        // Phase 1: 去除 HTML 标签（<...> 替换为空格，保留标签间文本）
        var sb = new StringBuilder(text.Length);
        var inTag = false;
        foreach (var ch in text)
        {
            if (ch == '<') { inTag = true; sb.Append(' '); continue; }
            if (ch == '>') { inTag = false; continue; }
            if (!inTag) sb.Append(ch);
        }

        // Phase 2: 解码 HTML 实体
        var decoded = WebUtility.HtmlDecode(sb.ToString());

        // Phase 3: 压缩空白
        return NormalizeWhitespace(decoded);
    }

    /// <summary>
    /// 规范化空白：连续空白字符合并为单个空格，去除首尾空白。
    /// </summary>
    private static string NormalizeWhitespace(string text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        var sb = new StringBuilder(text.Length);
        var i = 0;
        while (i < text.Length)
        {
            if (char.IsWhiteSpace(text[i]))
            {
                sb.Append(' ');
                while (i < text.Length && char.IsWhiteSpace(text[i]))
                    i++;
            }
            else
            {
                sb.Append(text[i]);
                i++;
            }
        }
        return sb.ToString().Trim();
    }

    private enum WebSearchProvider
    {
        Brave,
        DuckDuckGo,
    }

    private sealed record SearchHit(string Title, string Url, string? Snippet);
}
