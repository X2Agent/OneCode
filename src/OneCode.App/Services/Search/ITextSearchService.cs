namespace OneCode.App.Services.Search;

/// <summary>
/// 文本搜索请求参数。OutputMode 取值：files_with_matches / count / content（默认）。
/// Context 仅对 content 模式生效。
/// </summary>
public sealed record TextSearchRequest(
    string SearchPath,
    string Pattern,
    string? Glob = null,
    string? ExcludeGlob = null,
    bool CaseInsensitive = false,
    bool Multiline = false,
    string OutputMode = "content",
    int ContextBefore = 0,
    int ContextAfter = 0,
    string? WorkspaceRoot = null);

/// <summary>
/// 文本内容搜索内核——ripgrep 优先，C# Regex 兜底。GrepTool 与 FindReferencesTool 共用，
/// 消除两者各自重复实现的 ripgrep/native 扫描逻辑。
/// </summary>
public interface ITextSearchService
{
    /// <summary>
    /// 执行搜索并返回格式化结果行（content 模式为 "path:line:content"，
    /// files_with_matches 为路径，count 为 "path:count"）。
    /// </summary>
    Task<IReadOnlyList<string>> SearchAsync(TextSearchRequest request, CancellationToken ct = default);
}
