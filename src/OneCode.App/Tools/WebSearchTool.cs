using OneCode.Core.Config;
using OneCode.Core.Search;
using OneCode.Infrastructure.Config;
using System.ComponentModel;
using System.Net;
using System.Text;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;

namespace OneCode.App.Tools;

/// <summary>
/// WebSearch tool — 主打稳定性：按 <c>webSearchProvider</c> 设置组装
/// Tavily + DuckDuckGo 故障转移链，提供方失败或未配置 Key 时自动回退下一提供方。
///
/// <para>反爬防御：请求携带真实浏览器 User-Agent；DuckDuckGo 返回
/// HTTP 202 或 HTTP 200 但解析结果为空时均视为反爬挑战并报错，
/// 错误提示引导改用 BrowserFetch（内置 playwright MCP）而非反复重试。</para>
///
/// <para>同查询结果缓存 10 分钟，避免模型重复搜索浪费配额。</para>
/// </summary>
public sealed partial class WebSearchTool
{
    private const int MaxResults = 8;

    // 真实浏览器 UA：DuckDuckGo 对自标识 UA（OneCode/1.0）直接下发 202 反爬挑战（P0 修复）。
    private const string BrowserUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfigManager _config;
    private readonly ILogger<WebSearchTool> _logger;
    private readonly IReadOnlyList<IWebSearchProvider> _apiProviders;

    // 同查询结果缓存：10 分钟内重复搜索直接命中，节省 API 配额（Tavily 1000 次/月）。
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(10);
    private readonly Dictionary<string, (DateTimeOffset ExpiresAt, string ProviderLabel, IReadOnlyList<SearchHit> Hits)> _cache = new(StringComparer.Ordinal);

    // 单例复用 HtmlParser——DuckDuckGo HTML 解析专用。
    // AngleSharp 的 HtmlParser 是线程安全的（每次 ParseDocument 返回独立 IDocument），可安全共享。
    private static readonly HtmlParser _htmlParser = new();

    public WebSearchTool(
        IConfigManager config,
        IHttpClientFactory httpClientFactory,
        ILogger<WebSearchTool> logger,
        IEnumerable<IWebSearchProvider> apiProviders)
    {
        _config = config;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _apiProviders = apiProviders.ToArray();
    }

    private HttpClient CreateSearchHttpClient()
    {
        var client = _httpClientFactory.CreateClient(Constants.HttpClientNames.WebSearch);
        client.Timeout = TimeSpan.FromSeconds(Constants.Timeouts.WebSearch);
        // 反爬防御：携带真实浏览器指纹头，避免 DuckDuckGo 按非浏览器流量拦截（HTTP 202）。
        if (!client.DefaultRequestHeaders.UserAgent.TryParseAdd(BrowserUserAgent))
            throw new InvalidOperationException("Invalid browser User-Agent.");
        client.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
        return client;
    }

    [Description("Search the web for current information, returning a list of results with title, URL, and snippet. " +
                 "Use this to find up-to-date information beyond your knowledge cutoff (e.g. latest library versions, recent API changes, current events). " +
                 "Providers: Tavily API and DuckDuckGo HTML are tried in failover order (primary via the webSearchProvider setting; the other is the fallback — a provider without a configured Tavily key is skipped automatically). " +
                 "Domain filtering: allowed_domains restricts results to the listed domains (whitelist); blocked_domains excludes them (blacklist). Both accept bare hostnames (www. prefix is stripped automatically). " +
                 "Result limit: maximum 8 results per call. " +
                 "Query length: must be at least 2 characters. " +
                 "Tip: for reading a specific known URL, use WebFetch instead; for general research, use WebSearch first then WebFetch on the most relevant results. " +
                 "On total failure the error includes a hint — fall back to BrowserFetch (built-in playwright MCP) or WebFetch on a known URL instead of retrying the same query.")]
    public async Task<ToolResult> SearchAsync(
        [Description("The search query. Must be at least 2 characters. Use specific terms for better results; avoid overly broad queries like 'javascript'.")] string query,
        [Description("Whitelist: only include results from these domains. Example: ['docs.microsoft.com', 'github.com']. www. is stripped automatically. Omit for no whitelist.")] string[]? allowed_domains = null,
        [Description("Blacklist: exclude results from these domains. Example: ['w3schools.com', 'pinterest.com']. www. is stripped automatically. Omit for no blacklist.")] string[]? blocked_domains = null,
        CancellationToken ct = default)
    {
        if (query.Length < 2)
            return ToolResult.Error("query must be at least 2 characters");

        var startTime = DateTimeOffset.UtcNow;

        if (TryGetCached(query, allowed_domains, blocked_domains, out var cached))
        {
            return ToolResult.JsonSuccess(new
            {
                provider = cached.Provider,
                query,
                results = cached.Hits.Select(ToDto).ToList(),
                durationSeconds = (DateTimeOffset.UtcNow - startTime).TotalSeconds,
                cached = true,
            });
        }

        var failures = new List<string>();
        foreach (var (providerName, attempt) in BuildChain(_config.Current.Effective, query))
        {
            if (ct.IsCancellationRequested)
            {
                _logger.LogDebug("WebSearch '{Query}' cancelled", query);
                return ToolResult.Error("Request was cancelled");
            }

            try
            {
                var hits = await attempt(ct).ConfigureAwait(false);
                if (hits.Count == 0)
                    throw new InvalidOperationException("returned no results (possible anti-bot challenge or no matches). ");
                var filtered = FilterDomains(hits, allowed_domains, blocked_domains);
                CacheResults(query, filtered, providerName);
                return ToolResult.JsonSuccess(new
                {
                    provider = providerName,
                    query,
                    results = filtered.Select(ToDto).ToList(),
                    durationSeconds = (DateTimeOffset.UtcNow - startTime).TotalSeconds,
                });
            }
            catch (OperationCanceledException)
            {
                _logger.LogDebug("WebSearch '{Query}' cancelled (provider {Provider})", query, providerName);
                return ToolResult.Error("Request was cancelled");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "WebSearch '{Query}' failed via {Provider}, trying next provider", query, providerName);
                failures.Add($"{providerName}: {ex.Message}");
            }
        }

        // 全部提供方失败：降级引导（决策权归模型），指向 BrowserFetch 而非重复重试。
        var detail = string.Join("; ", failures);
        _logger.LogWarning("WebSearch '{Query}' failed on all providers", query);
        return ToolResult.Error($"WebSearch error: all providers failed. {detail}\n\n" +
            "Hint: do not retry the same query repeatedly; rephrase once, use WebFetch on a known URL, " +
            "or use BrowserFetch (built-in playwright MCP browser tools) for interactive rendering.");
    }

    /// <summary>
    /// 组装故障转移链：按 <c>webSearchProvider</c> 设置决定首选顺序。
    /// 主提供方失败（或未配置 Key）时自动回退备选提供方；
    /// 无 Tavily Key 时 Tavily 环节被跳过（未注册 IWebSearchProvider 亦跳过，便于测试）。
    /// </summary>
    private IEnumerable<(string Name, Func<CancellationToken, Task<IReadOnlyList<SearchHit>>> Attempt)> BuildChain(
        AppSettings settings, string query)
    {
        var tavily = _apiProviders.FirstOrDefault(p => p.Name.Equals("tavily", StringComparison.OrdinalIgnoreCase));
        var primary = settings.WebSearchProvider.Equals("tavily", StringComparison.OrdinalIgnoreCase)
            ? "tavily"
            : "duckduckgo";

        foreach (var name in PrimaryFirst(primary))
        {
            if (name == "tavily")
            {
                if (tavily is { IsConfigured: true })
                    yield return (tavily.Name,
                        async token => (await tavily.SearchAsync(query, MaxResults, token).ConfigureAwait(false))
                            .Select(h => new SearchHit(h.Title, h.Url, h.Snippet)).ToList());
            }
            else
            {
                yield return ("duckduckgo", token => SearchDuckDuckGoAsync(query, token));
            }
        }
    }

    private static IEnumerable<string> PrimaryFirst(string primary) =>
        primary == "tavily" ? (string[])["tavily", "duckduckgo"] : (string[])["duckduckgo", "tavily"];

    private static object ToDto(SearchHit hit) => new { title = hit.Title, url = hit.Url, snippet = hit.Snippet };

    private bool TryGetCached(
        string query,
        string[]? allowedDomains,
        string[]? blockedDomains,
        out (string Provider, IReadOnlyList<SearchHit> Hits) entry)
    {
        if (_cache.TryGetValue(query, out var cached) && cached.ExpiresAt > DateTimeOffset.UtcNow)
        {
            entry = (cached.ProviderLabel, FilterDomains(cached.Hits, allowedDomains, blockedDomains));
            return true;
        }

        if (_cache.TryGetValue(query, out var expired) && expired.ExpiresAt <= DateTimeOffset.UtcNow)
            _cache.Remove(query);

        entry = default;
        return false;
    }

    private void CacheResults(string query, IReadOnlyList<SearchHit> hits, string providerName) =>
        _cache[query] = (DateTimeOffset.UtcNow.Add(CacheTtl), providerName, hits);

    private async Task<IReadOnlyList<SearchHit>> SearchDuckDuckGoAsync(string query, CancellationToken ct)
    {
        var url = $"https://html.duckduckgo.com/html/?q={Uri.EscapeDataString(query)}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using var httpClient = CreateSearchHttpClient();
        using var response = await httpClient.SendAsync(request, ct).ConfigureAwait(false);

        // DuckDuckGo 反爬挑战：html.duckduckgo.com 的异常检测返回 202 + 无结果锚点的页面。
        // EnsureSuccessStatusCode 对 2xx 放行，若不显式报错会得到"成功但空结果"，
        // 导致模型误以为搜索正常而反复换词重试。200 + 空解析结果同属挑战特征，
        // 由 SearchAsync 统一判空抛错并回退下一提供方。
        if (response.StatusCode == HttpStatusCode.Accepted)
            throw new InvalidOperationException("DuckDuckGo returned an anti-bot challenge (HTTP 202, no results). ");

        response.EnsureSuccessStatusCode();

        var html = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return ParseDuckDuckGoResults(html);
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
    ///   <item>用 <c>WebUtility.HtmlDecode</c> 解码 HTML 实体（&amp;amp; → &amp; 等）</item>
    ///   <item>用 <see cref="NormalizeWhitespace"/> 压缩连续空白为单个空格</item>
    /// </list>
    /// </summary>
    // 仅单元测试使用：生产代码当前无调用方（测试接缝）。
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

    private sealed record SearchHit(string Title, string Url, string? Snippet);
}
