using OneCode.Core.Config;
using OneCode.Core.Search;
using System.ComponentModel;
using Microsoft.Extensions.Caching.Memory;

namespace OneCode.App.Tools;

/// <summary>
/// WebSearch tool — 按 <c>webSearchProvider</c> 设置组装提供方故障转移链，
/// 主提供方失败或未配置时自动回退下一提供方。
///
/// <para><b>职责边界</b>：提供方适配（Tavily / DuckDuckGo 的 HTTP 调用与解析）在 Infrastructure，
/// 本工具只负责排序、回退、域过滤与缓存。两个提供方实现同一个 <see cref="IWebSearchProvider"/>，
/// 因此替换或新增后端不需要改这个工具。</para>
///
/// <para>同查询结果缓存 10 分钟（存原始结果，域过滤每次独立应用），避免重复搜索浪费配额。</para>
/// </summary>
public sealed class WebSearchTool
{
    private const int MaxResults = 8;

    private readonly IConfigManager _config;
    private readonly ILogger<WebSearchTool> _logger;
    private readonly IReadOnlyList<IWebSearchProvider> _apiProviders;
    private readonly IMemoryCache _cache;

    /// <summary>
    /// 缓存键前缀。缓存的是提供方返回的**原始**结果，不含域过滤：
    /// 域条件属于单次调用的过滤参数，把它编进键会让同一 query 因白名单变化而反复回源，
    /// 且旧实现把已过滤结果当缓存值，放宽域条件时会永远拿不到被滤掉的候选。
    /// </summary>
    private const string CacheKeyPrefix = "websearch:";

    // 同 query 结果缓存：10 分钟内重复搜索直接命中，节省 API 配额（Tavily 1000 次/月）。
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(10);

    // 单条缓存上限：限制无同步字典的旧实现遗留的无界增长风险。
    private const int CacheEntrySize = 1;

    public WebSearchTool(
        IConfigManager config,
        ILogger<WebSearchTool> logger,
        IEnumerable<IWebSearchProvider> apiProviders,
        IMemoryCache cache)
    {
        _config = config;
        _logger = logger;
        _apiProviders = apiProviders.ToArray();
        _cache = cache;
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

        // The cache holds unfiltered provider output, so domain conditions are applied per call.
        // Narrowing and then widening a whitelist must both see the same candidate set.
        if (TryGetCached(query, out var cached))
        {
            return ToolResult.JsonSuccess(new
            {
                provider = cached.Provider,
                query,
                results = FilterDomains(cached.Hits, allowed_domains, blocked_domains).Select(ToDto).ToList(),
                durationSeconds = (DateTimeOffset.UtcNow - startTime).TotalSeconds,
                cached = true,
            });
        }

        var failures = new List<string>();
        foreach (var (providerName, attempt) in BuildChain(_config.Current.Effective, query))
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var hits = await attempt(ct).ConfigureAwait(false);

                // An empty page is not a valid result: DuckDuckGo answers anti-bot challenges with
                // an empty parse, and caching that would pin the failure for the whole TTL.
                if (hits.Count == 0)
                    throw new InvalidOperationException("returned no results (possible anti-bot challenge or no matches).");

                CacheResults(query, hits, providerName);
                return ToolResult.JsonSuccess(new
                {
                    provider = providerName,
                    query,
                    results = FilterDomains(hits, allowed_domains, blocked_domains).Select(ToDto).ToList(),
                    durationSeconds = (DateTimeOffset.UtcNow - startTime).TotalSeconds,
                });
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Caller cancellation is not a provider failure and must not trigger failover:
                // moving to the next provider would keep issuing outbound requests after the stop.
                _logger.LogDebug("WebSearch cancelled by caller (provider {Provider})", providerName);
                throw;
            }
            catch (OperationCanceledException)
            {
                // The token was not cancelled, so this is the HTTP client's own timeout.
                // A timeout is a provider failure: fall through to the next provider.
                _logger.LogWarning("WebSearch timed out via {Provider}, trying next provider", providerName);
                failures.Add($"{providerName}: timed out");
            }
            catch (Exception ex)
            {
                // The query text and the provider's raw error are deliberately not logged: both can
                // carry user content or credentials echoed back by the service.
                _logger.LogWarning(ex, "WebSearch failed via {Provider}, trying next provider", providerName);
                failures.Add($"{providerName}: {ClassifyFailure(ex)}");
            }
        }

        // 全部提供方失败：降级引导（决策权归模型），指向 BrowserFetch 而非重复重试。
        var detail = string.Join("; ", failures);
        _logger.LogWarning("WebSearch failed on all {Count} provider(s)", failures.Count);
        return ToolResult.Error($"WebSearch error: all providers failed. {detail}\n\n" +
            "Hint: do not retry the same query repeatedly; rephrase once, use WebFetch on a known URL, " +
            "or use BrowserFetch (built-in playwright MCP browser tools) for interactive rendering.");
    }

    /// <summary>
    /// 组装故障转移链：按 <c>webSearchProvider</c> 设置决定首选顺序。
    /// 主提供方失败（或未配置 Key）时自动回退备选提供方；
    /// 无 Tavily Key 时 Tavily 环节被跳过（未注册 IWebSearchProvider 亦跳过，便于测试）。
    /// </summary>
    /// <remarks>
    /// 两个提供方都实现同一个 <see cref="IWebSearchProvider"/>，包括 DuckDuckGo——
    /// 它以前内联在本工具里，导致工具同时承担「提供方适配」与「故障转移编排」两件事。
    /// </remarks>
    private IEnumerable<(string Name, Func<CancellationToken, Task<IReadOnlyList<SearchHit>>> Attempt)> BuildChain(
        AppSettings settings, string query)
    {
        var tavily = FindProvider("tavily");
        var duckDuckGo = FindProvider("duckduckgo");
        var primary = settings.WebSearchProvider.Equals("tavily", StringComparison.OrdinalIgnoreCase)
            ? "tavily"
            : "duckduckgo";

        foreach (var name in PrimaryFirst(primary))
        {
            var provider = name == "tavily" ? tavily : duckDuckGo;
            if (provider is not { IsConfigured: true })
                continue;

            yield return (provider.Name, async token =>
                (await provider.SearchAsync(query, MaxResults, token).ConfigureAwait(false))
                    .Select(hit => new SearchHit(hit.Title, hit.Url, hit.Snippet))
                    .ToList());
        }
    }

    private IWebSearchProvider? FindProvider(string name) => _apiProviders
        .FirstOrDefault(provider => provider.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<string> PrimaryFirst(string primary) =>
        primary == "tavily" ? (string[])["tavily", "duckduckgo"] : (string[])["duckduckgo", "tavily"];

    private static object ToDto(SearchHit hit) => new { title = hit.Title, url = hit.Url, snippet = hit.Snippet };

    /// <summary>
    /// Reads the <b>unfiltered</b> provider results for a query.
    /// </summary>
    /// <remarks>
    /// The domain filters are deliberately not part of the key: they are per-call presentation
    /// parameters, not a property of the search. Keying on them meant a narrower whitelist populated
    /// the cache with a pruned candidate set that a later wider query could never recover.
    /// </remarks>
    private bool TryGetCached(string query, out (string Provider, IReadOnlyList<SearchHit> Hits) entry)
    {
        if (_cache.TryGetValue(CacheKeyPrefix + query, out var cached)
            && cached is (string Provider, IReadOnlyList<SearchHit> Hits))
        {
            entry = (Provider, Hits);
            return true;
        }

        entry = default;
        return false;
    }

    private void CacheResults(string query, IReadOnlyList<SearchHit> hits, string providerName) =>
        _cache.Set(
            CacheKeyPrefix + query,
            (providerName, hits),
            new MemoryCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = CacheTtl,
                // The cache is shared and size-limited; without a size the entry bypasses the limit.
                Size = CacheEntrySize,
            });

    /// <summary>
    /// Maps a provider failure to a stable category. The provider's raw message is not surfaced:
    /// it can echo the query, request headers or credentials, and the model only needs to know
    /// whether retrying or switching approach is worthwhile.
    /// </summary>
    private static string ClassifyFailure(Exception ex) => ex switch
    {
        HttpRequestException { StatusCode: System.Net.HttpStatusCode.TooManyRequests } => "rate limited",
        HttpRequestException { StatusCode: System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden } => "authentication failed",
        HttpRequestException => "network error",
        InvalidOperationException => "no usable results",
        _ => "failed",
    };

    private sealed record SearchHit(string Title, string Url, string? Snippet);

    /// <summary>
    /// Applies the per-call domain conditions and caps the result count.
    /// </summary>
    /// <remarks>
    /// Applied on every call rather than at cache time: the cache holds unfiltered provider output, so a
    /// narrowed whitelist must not be able to permanently prune the candidate set.
    /// </remarks>
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
            if (allowed.Length > 0
                && !allowed.Any(domain => host == domain || host.EndsWith($".{domain}", StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }

            if (blocked.Any(domain => host == domain || host.EndsWith($".{domain}", StringComparison.OrdinalIgnoreCase)))
                return false;

            return true;
        }).Take(MaxResults).ToList();
    }

    /// <summary>Normalizes a hostname for comparison: trims dots, strips a leading <c>www.</c>, lowercases.</summary>
    private static string NormalizeDomain(string domain)
    {
        var trimmed = domain.Trim().Trim('.');
        return trimmed.StartsWith("www.", StringComparison.OrdinalIgnoreCase)
            ? trimmed[4..].ToLowerInvariant()
            : trimmed.ToLowerInvariant();
    }
}
