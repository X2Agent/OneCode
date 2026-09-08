using System.Net;
using System.Net.Http.Headers;
using OneCode.Core.Config;
using OneCode.Core.Search;
using InfrastructureConstants = OneCode.Infrastructure.Config.Constants;

namespace OneCode.Infrastructure.Search;

/// <summary>
/// Tavily Search API 适配（<see href="https://api.tavily.com/search"/>）。
///
/// <para>请求：<c>POST</c> JSON <c>{ query, max_results, search_depth }</c>，
/// <c>Authorization: Bearer tvly-...</c>；响应 <c>results[].title/url/content</c>，
/// content 映射为 <see cref="WebSearchResult.Snippet"/>。</para>
///
/// <para>API Key 解析完全走 <see cref="IConfigManager"/> 有效快照：
/// <c>TAVILY_API_KEY</c> 环境变量经 SettingDescriptor 映射为
/// <c>webSearchApiKeys.tavily</c>（Environment 作用域），优先级高于用户/项目配置。</para>
///
/// <para>错误映射：401/403 → Key 无效；429 → 配额耗尽；均抛
/// <see cref="InvalidOperationException"/>，由 App 层 WebSearchTool 捕获后回退下一提供方。</para>
/// </summary>
public sealed class TavilySearchProvider(IHttpClientFactory httpClientFactory, IConfigManager config) : IWebSearchProvider
{
    private const string SearchDepth = "basic";

    /// <inheritdoc />
    public string Name => "tavily";

    /// <inheritdoc />
    public bool IsConfigured => !string.IsNullOrWhiteSpace(config.Current.Effective.TavilyApiKey);

    /// <inheritdoc />
    public async Task<IReadOnlyList<WebSearchResult>> SearchAsync(string query, int maxResults, CancellationToken ct = default)
    {
        var apiKey = config.Current.Effective.TavilyApiKey;
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException(
                "Tavily search requires an API key. Set TAVILY_API_KEY or configure 'webSearchApiKeys.tavily' via /config set or the /settings overlay.");

        var client = httpClientFactory.CreateClient(InfrastructureConstants.HttpClientNames.WebSearch);
        using var request = new HttpRequestMessage(HttpMethod.Post, InfrastructureConstants.Urls.TavilySearch);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Content = new StringContent(
            JsonSerializer.Serialize(new { query, max_results = maxResults, search_depth = SearchDepth }),
            Encoding.UTF8,
            "application/json");

        using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new InvalidOperationException(
                $"Tavily rejected the API key (HTTP {(int)response.StatusCode}). Verify TAVILY_API_KEY / webSearchApiKeys.tavily.");
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
            throw new InvalidOperationException(
                "Tavily quota exceeded (HTTP 429). Wait or check https://app.tavily.com for usage.");

        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);

        List<WebSearchResult> hits = [];
        if (json.RootElement.TryGetProperty("results", out var results)
            && results.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in results.EnumerateArray())
            {
                var title = item.TryGetProperty("title", out var titleEl) ? titleEl.GetString() : null;
                var url = item.TryGetProperty("url", out var urlEl) ? urlEl.GetString() : null;
                var snippet = item.TryGetProperty("content", out var contentEl) ? contentEl.GetString() : null;

                if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(url))
                    continue;

                hits.Add(new WebSearchResult(title, url, snippet));
            }
        }

        return hits;
    }
}
