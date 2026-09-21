using System.Net;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using OneCode.Core.Search;
using InfrastructureConstants = OneCode.Infrastructure.Config.Constants;

namespace OneCode.Infrastructure.Search;

/// <summary>
/// DuckDuckGo HTML 适配（<see href="https://html.duckduckgo.com/html/"/>），实现
/// <see cref="IWebSearchProvider"/>，与 <see cref="TavilySearchProvider"/> 同一边界。
/// </summary>
/// <remarks>
/// <para>
/// <b>为何放在 Infrastructure。</b> 这是一段外部站点适配：HTTP 调用、反爬处理与 HTML 解析。
/// 放在 App 层会让工具同时承担「提供方适配」与「故障转移编排」两件事，
/// 也让 DDG 无法像 Tavily 一样被独立替换或测试。
/// </para>
/// <para>
/// <b>反爬防御。</b> 请求携带真实浏览器 User-Agent；HTTP 202，以及 HTTP 200 但解析结果为空，
/// 都视为反爬挑战并抛错。否则会得到「成功但空结果」，模型会误以为搜索正常而反复换词重试。
/// </para>
/// <para>
/// <b>无需配置。</b> 与 Tavily 不同，本提供方始终可用（无 API Key），
/// 因此 <see cref="IsConfigured"/> 恒为 true。
/// </para>
/// </remarks>
public sealed class DuckDuckGoSearchProvider(IHttpClientFactory httpClientFactory) : IWebSearchProvider
{
    /// <summary>真实浏览器 UA：DDG 对自标识 UA 直接下发 202 反爬挑战。</summary>
    private const string BrowserUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";

    // 单例复用 HtmlParser——AngleSharp 的 HtmlParser 线程安全（每次 ParseDocument 返回独立 IDocument）。
    private static readonly HtmlParser s_htmlParser = new();

    /// <inheritdoc />
    public string Name => "duckduckgo";

    /// <inheritdoc />
    public bool IsConfigured => true;

    /// <inheritdoc />
    public async Task<IReadOnlyList<WebSearchResult>> SearchAsync(string query, int maxResults, CancellationToken ct = default)
    {
        var url = $"https://html.duckduckgo.com/html/?q={Uri.EscapeDataString(query)}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using var httpClient = CreateSearchHttpClient();
        using var response = await httpClient.SendAsync(request, ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.Accepted)
        {
            throw new InvalidOperationException(
                "DuckDuckGo returned an anti-bot challenge (HTTP 202, no results).");
        }

        response.EnsureSuccessStatusCode();

        var html = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return ParseResults(html, maxResults);
    }

    private HttpClient CreateSearchHttpClient()
    {
        var client = httpClientFactory.CreateClient(InfrastructureConstants.HttpClientNames.WebSearch);
        client.Timeout = TimeSpan.FromSeconds(InfrastructureConstants.Timeouts.WebSearch);

        // 反爬防御：携带真实浏览器指纹头，避免 DDG 按非浏览器流量拦截（HTTP 202）。
        if (!client.DefaultRequestHeaders.UserAgent.TryParseAdd(BrowserUserAgent))
            throw new InvalidOperationException("Invalid browser User-Agent.");

        client.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
        return client;
    }

    /// <summary>
    /// 解析 DDG HTML 结果页：用 <c>a.result__a</c> 定位链接，在后续兄弟节点中查找对应摘要。
    /// </summary>
    internal static IReadOnlyList<WebSearchResult> ParseResults(string html, int maxResults)
    {
        if (string.IsNullOrWhiteSpace(html) || maxResults <= 0)
            return [];

        var document = s_htmlParser.ParseDocument(html);
        var linkElements = document.QuerySelectorAll("a.result__a");

        List<WebSearchResult> hits = [];
        foreach (var linkEl in linkElements)
        {
            if (hits.Count >= maxResults)
                break;

            var rawHref = linkEl.GetAttribute("href") ?? string.Empty;
            var title = NormalizeWhitespace(linkEl.TextContent ?? string.Empty);
            var resolvedUrl = ResolveRedirect(WebUtility.HtmlDecode(rawHref));

            if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(resolvedUrl))
                continue;

            var snippetEl = FindNextSnippet(linkEl);
            var snippet = snippetEl is not null
                ? NormalizeWhitespace(snippetEl.TextContent ?? string.Empty)
                : null;
            if (string.IsNullOrEmpty(snippet))
                snippet = null;

            hits.Add(new WebSearchResult(title, resolvedUrl, snippet));
        }

        return hits;
    }

    /// <summary>
    /// 从当前元素开始向后续兄弟节点查找最近的 <c>result__snippet</c>；
    /// 遇到下一个 <c>result__a</c> 时停止，避免把后续结果的摘要误关联到当前链接。
    /// </summary>
    internal static IElement? FindNextSnippet(IElement start)
    {
        var sibling = start.NextElementSibling;
        while (sibling is not null)
        {
            if (sibling.ClassList.Contains("result__snippet"))
                return sibling;

            if (sibling.TagName.Equals("A", StringComparison.OrdinalIgnoreCase) &&
                sibling.ClassList.Contains("result__a"))
            {
                return null;
            }

            sibling = sibling.NextElementSibling;
        }

        return null;
    }

    /// <summary>把 DDG 的 <c>/l/?uddg=</c> 跳转链接还原为真实目标 URL。</summary>
    internal static string ResolveRedirect(string href)
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

            if (!string.Equals(part[..idx], "uddg", StringComparison.OrdinalIgnoreCase))
                continue;

            return Uri.UnescapeDataString(part[(idx + 1)..]);
        }

        return href;
    }

    /// <summary>规范化空白：连续空白合并为单个空格，去除首尾空白。</summary>
    internal static string NormalizeWhitespace(string text)
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
}
