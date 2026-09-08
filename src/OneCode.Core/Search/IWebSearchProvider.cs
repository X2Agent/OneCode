namespace OneCode.Core.Search;

/// <summary>
/// 单条网页搜索结果。Title/Url 必填，Snippet 可为 null（提供方未返回摘要时）。
/// </summary>
public sealed record WebSearchResult(string Title, string Url, string? Snippet);

/// <summary>
/// WebSearch 提供方抽象：由 Infrastructure 层实现外部搜索 API 适配（如 Tavily），
/// App 层的 <c>WebSearchTool</c> 按 <c>webSearchProvider</c> 设置组装故障转移链并逐个尝试。
/// </summary>
public interface IWebSearchProvider
{
    /// <summary>提供方小写标识（与 <c>webSearchProvider</c> 设置值一致，如 <c>tavily</c>）。</summary>
    string Name { get; }

    /// <summary>当前是否已具备调用条件（如 API Key 已配置）。未就绪的提供方在链路中被跳过。</summary>
    bool IsConfigured { get; }

    /// <summary>执行搜索。失败（网络、认证、限流等）抛异常，由调用方决定是否回退下一提供方。</summary>
    Task<IReadOnlyList<WebSearchResult>> SearchAsync(string query, int maxResults, CancellationToken ct = default);
}
