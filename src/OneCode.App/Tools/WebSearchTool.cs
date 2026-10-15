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
public sealed class WebSearchTool(
    IConfigManager config,
    ILogger<WebSearchTool> logger,
    IEnumerable<IWebSearchProvider> apiProviders,
    IMemoryCache cache)
{
    private const int MaxResults = 8;

    // 单条搜索摘要的内联长度上限：保证最多 8 条结果可以完整内联返回（~3–5 KB），
    // 模型无需额外发起 Read 工具往返即可浏览全部候选。
    public const int MaxSnippetChars = 600;

    private readonly IConfigManager _config = config;
    private readonly ILogger<WebSearchTool> _logger = logger;
    private readonly IReadOnlyList<IWebSearchProvider> _apiProviders = apiProviders.ToArray();
    private readonly IMemoryCache _cache = cache;

    /// <summary>
    /// 缓存键前缀。缓存的是提供方返回的**原始**结果，不含域过滤：
    /// 域条件属于单次调用的过滤参数，把它编进键会让同一 query 因白名单变化而反复回源，
    /// 缓存已过滤结果则会使后续放宽域条件时拿不到此前被滤掉的候选。
    /// </summary>
    private const string CacheKeyPrefix = "websearch:";
    private const string CooldownKeyPrefix = "websearch:cooldown:";

    // 同 query 结果缓存：10 分钟内重复搜索直接命中，节省 API 配额（Tavily 1000 次/月）。
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(10);

    // 提供方级故障冷却：配额耗尽/鉴权失败冷却 5 分钟，反爬挑战冷却 1 分钟，防止并发批次盲目重锤已挂提供方。
    private static readonly TimeSpan QuotaOrAuthCooldownTtl = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan AntiBotCooldownTtl = TimeSpan.FromMinutes(1);

    // 单条缓存尺寸权重，配合共享 IMemoryCache 的 SizeLimit 控制缓存容量。
    private const int CacheEntrySize = 1;

    [Description("Search the web for current information, returning a list of results with title, URL, and snippet. " +
                 "Use this to find up-to-date information beyond your knowledge cutoff (e.g. latest library versions, recent API changes, current events). " +
                 "Providers: Tavily API and DuckDuckGo HTML are tried in failover order (primary via the webSearchProvider setting; the other is the fallback — a provider without a configured Tavily key is skipped automatically). " +
                 "Domain filtering: allowed_domains restricts results to the listed domains (whitelist); blocked_domains excludes them (blacklist). Both accept bare hostnames (www. prefix is stripped automatically). " +
                 "Result limit: maximum 8 results per call (each snippet capped at 600 chars). " +
                 "Query length: must be at least 2 characters. " +
                 "Tip: for reading a specific known URL, use WebFetch instead; for general research, use WebSearch first then WebFetch on the most relevant results. " +
                 "On total failure the error includes a hint — fall back to BrowserFetch (built-in playwright MCP) or WebFetch on a known URL instead of retrying the same query.")]
    public async Task<ToolResult> SearchAsync(
        [Description("The search query. Must be at least 2 characters. Use specific terms for better results; avoid overly broad queries like 'javascript'. " +
                     "The current date is in your system context — when the user asks about recent/upcoming events, anchor the query on the CURRENT year and month (e.g. \"最近\" means the current month), never on dates remembered from training data.")] string query,
        [Description("Whitelist: only include results from these domains. Example: [\"docs.microsoft.com\", \"github.com\"]. www. is stripped automatically. Omit for no whitelist.")] string[]? allowed_domains = null,
        [Description("Blacklist: exclude results from these domains. Example: [\"w3schools.com\", \"pinterest.com\"]. www. is stripped automatically. Omit for no blacklist.")] string[]? blocked_domains = null,
        CancellationToken ct = default)
    {
        if (query.Length < 2)
            return ToolResult.Error("query must be at least 2 characters");

        var startTime = DateTimeOffset.UtcNow;

        // The cache holds unfiltered provider output, so domain conditions are applied per call.
        // Narrowing and then widening a whitelist must both see the same candidate set.
        if (TryGetCached(query, out var cached))
        {
            var filteredCached = FilterDomains(cached.Hits, allowed_domains, blocked_domains);
            return ToolResult.JsonSuccess(new
            {
                provider = cached.Provider,
                query,
                results = filteredCached.Select(ToDto).ToList(),
                resultCount = filteredCached.Count,
                durationSeconds = (DateTimeOffset.UtcNow - startTime).TotalSeconds,
                cached = true,
            });
        }

        List<string> failures = [];
        var attemptedLiveCall = false;
        foreach (var (providerName, attempt) in BuildChain(_config.Current.Effective, query))
        {
            ct.ThrowIfCancellationRequested();

            if (TryGetProviderCooldown(providerName, out var cooldownCategory))
            {
                failures.Add($"{providerName}: {cooldownCategory}");
                continue;
            }

            attemptedLiveCall = true;
            try
            {
                var hits = await attempt(ct).ConfigureAwait(false);

                // An empty page is not a valid result: DuckDuckGo answers anti-bot challenges with
                // an empty parse, and caching that would pin the failure for the whole TTL.
                if (hits.Count == 0)
                    throw new InvalidOperationException("returned no results (possible anti-bot challenge or no matches).");

                CacheResults(query, hits, providerName);
                var filtered = FilterDomains(hits, allowed_domains, blocked_domains);
                return ToolResult.JsonSuccess(new
                {
                    provider = providerName,
                    query,
                    results = filtered.Select(ToDto).ToList(),
                    resultCount = filtered.Count,
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
                var category = ClassifyFailure(ex);
                if (TryGetCooldownDuration(ex, out var cooldownTtl))
                    SetProviderCooldown(providerName, category, cooldownTtl);
                failures.Add($"{providerName}: {category}");
            }
        }

        // 全部提供方失败：明确要求模型停止继续调用 WebSearch，直接使用已有上下文继续任务。
        var detail = string.Join("; ", failures);
        var cooldownNotice = !attemptedLiveCall && failures.Count > 0
            ? " All configured search providers are currently in cooldown (circuit breaker active); changing or simplifying the query will NOT bypass cooldown."
            : string.Empty;
        _logger.LogWarning("WebSearch failed on all {Count} provider(s) (liveAttempt={AttemptedLiveCall})", failures.Count, attemptedLiveCall);
        return ToolResult.Error(
            $"WebSearch error: all providers failed. {detail}{cooldownNotice}\n\n" +
            "Hint: search providers are currently unavailable. Do NOT retry WebSearch or rephrase the query; " +
            "continue the task using the information already in context, or use WebFetch on a specific known URL.",
            suggestedNextAction: "Stop calling WebSearch in this turn; use WebFetch on known URLs or proceed with existing context.");
    }

    /// <summary>
    /// 组装故障转移链：按 <c>webSearchProvider</c> 设置决定首选顺序。
    /// 主提供方失败（或未配置 Key）时自动回退备选提供方；
    /// 无 Tavily Key 时 Tavily 环节被跳过（未注册 IWebSearchProvider 亦跳过，便于测试）。
    /// </summary>
    private IEnumerable<(string Name, Func<CancellationToken, Task<IReadOnlyList<WebSearchResult>>> Attempt)> BuildChain(
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

            yield return (provider.Name, token => provider.SearchAsync(query, MaxResults, token));
        }
    }

    private IWebSearchProvider? FindProvider(string name) => _apiProviders
        .FirstOrDefault(provider => provider.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<string> PrimaryFirst(string primary) =>
        primary == "tavily" ? (string[])["tavily", "duckduckgo"] : (string[])["duckduckgo", "tavily"];

    private static object ToDto(WebSearchResult hit) => new
    {
        title = hit.Title,
        url = hit.Url,
        snippet = hit.Snippet is { Length: > MaxSnippetChars } s ? s[..MaxSnippetChars] + "…" : hit.Snippet,
    };

    /// <summary>
    /// Reads the <b>unfiltered</b> provider results for a query.
    /// </summary>
    /// <remarks>
    /// The domain filters are deliberately not part of the key: they are per-call presentation
    /// parameters, not a property of the search. Keying on them meant a narrower whitelist populated
    /// the cache with a pruned candidate set that a later wider query could never recover.
    /// </remarks>
    private bool TryGetCached(string query, out (string Provider, IReadOnlyList<WebSearchResult> Hits) entry)
    {
        if (_cache.TryGetValue(CacheKeyPrefix + query, out var cached)
            && cached is (string Provider, IReadOnlyList<WebSearchResult> Hits))
        {
            entry = (Provider, Hits);
            return true;
        }

        entry = default;
        return false;
    }

    private void CacheResults(string query, IReadOnlyList<WebSearchResult> hits, string providerName) =>
        _cache.Set(
            CacheKeyPrefix + query,
            (providerName, hits),
            new MemoryCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = CacheTtl,
                // The cache is shared and size-limited; without a size the entry bypasses the limit.
                Size = CacheEntrySize,
            });

    private bool TryGetProviderCooldown(string providerName, out string category)
    {
        if (_cache.TryGetValue(CooldownKeyPrefix + providerName, out var cached)
            && cached is string reason)
        {
            category = reason;
            return true;
        }

        category = string.Empty;
        return false;
    }

    private void SetProviderCooldown(string providerName, string category, TimeSpan ttl) =>
        _cache.Set(
            CooldownKeyPrefix + providerName,
            category,
            new MemoryCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = ttl,
                Size = CacheEntrySize,
            });

    private static bool TryGetCooldownDuration(Exception ex, out TimeSpan ttl)
    {
        if (ex is HttpRequestException { StatusCode: { } status })
        {
            var code = (int)status;
            if (code is 401 or 403 or 429 or 432)
            {
                ttl = QuotaOrAuthCooldownTtl;
                return true;
            }
        }

        if (ex is InvalidOperationException)
        {
            var msg = ex.Message;
            if (msg.Contains("HTTP 401", StringComparison.Ordinal)
                || msg.Contains("HTTP 403", StringComparison.Ordinal)
                || msg.Contains("HTTP 429", StringComparison.Ordinal)
                || msg.Contains("HTTP 432", StringComparison.Ordinal))
            {
                ttl = QuotaOrAuthCooldownTtl;
                return true;
            }

            if (msg.Contains("HTTP 202", StringComparison.Ordinal))
            {
                ttl = AntiBotCooldownTtl;
                return true;
            }
        }

        ttl = default;
        return false;
    }

    /// <summary>
    /// Maps a provider failure to a stable category. The provider's raw message is not surfaced:
    /// it can echo the query, request headers or credentials, and the model only needs to know
    /// whether retrying or switching approach is worthwhile.
    /// </summary>
    private static string ClassifyFailure(Exception ex) => ex switch
    {
        HttpRequestException { StatusCode: System.Net.HttpStatusCode.TooManyRequests or (System.Net.HttpStatusCode)432 } => "rate limited",
        HttpRequestException { StatusCode: System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden } => "authentication failed",
        HttpRequestException => "network error",
        InvalidOperationException => "no usable results",
        _ => "failed",
    };

    /// <summary>
    /// Applies the per-call domain conditions and caps the result count.
    /// </summary>
    /// <remarks>
    /// Applied on every call rather than at cache time: the cache holds unfiltered provider output, so a
    /// narrowed whitelist must not be able to permanently prune the candidate set.
    /// </remarks>
    private static IReadOnlyList<WebSearchResult> FilterDomains(
        IEnumerable<WebSearchResult> hits,
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
