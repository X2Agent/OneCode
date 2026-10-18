namespace OneCode.Core.Lsp;

/// <summary>
/// 索引过程中发现的代码符号。
/// </summary>
/// <param name="Name">符号名（类名、方法名等）。</param>
/// <param name="Kind">符号种类（类、方法、接口、枚举等）。</param>
/// <param name="FilePath">符号所在文件的绝对路径。</param>
/// <param name="Line">符号声明所在行号，从 1 开始。</param>
/// <param name="Column">符号起始列号，从 1 开始。</param>
public sealed record CodeSymbol(
    string Name,
    string Kind,
    string FilePath,
    int Line,
    int Column);

/// <summary>
/// 符号查找结果。<c>RelevanceScore</c> 为 1.0 表示精确匹配。
/// </summary>
public sealed record CodeSymbolMatch(CodeSymbol Symbol, double RelevanceScore);

/// <summary>
/// 代码符号索引服务：扫描源文件并建立倒排索引，用于快速查找符号。
/// </summary>
public interface ICodeIndexService
{
    /// <summary>索引当前是否正在构建。</summary>
    bool IsIndexing { get; }

    /// <summary>已索引的符号总数。</summary>
    int SymbolCount { get; }

    /// <summary>最近一次索引完成时间；从未索引时为 null。</summary>
    DateTimeOffset? LastIndexedAt { get; }

    /// <summary>
    /// 为指定根目录构建或重建完整索引。
    /// </summary>
    Task BuildIndexAsync(string rootDirectory, CancellationToken ct = default);

    /// <summary>
    /// 按变更文件更新索引（新增、修改、删除）。
    /// </summary>
    Task UpdateFilesAsync(IEnumerable<string> changedFiles, IEnumerable<string>? removedFiles = null, CancellationToken ct = default);

    /// <summary>
    /// 查找匹配查询的符号，结果按相关度排序（精确匹配 &gt; 前缀匹配 &gt; 子串匹配）。
    /// </summary>
    /// <param name="query">要查找的符号名，支持部分匹配。</param>
    /// <param name="maxResults">返回结果的最大数量。</param>
    /// <param name="kindFilter">
    /// 可选的符号种类过滤（如 <c>class</c>、<c>method</c>、<c>interface</c>、<c>enum</c>、<c>function</c>），
    /// 不区分大小写；为 null 时返回全部种类。
    /// </param>
    /// <param name="pathScope">
    /// 可选的目录或文件路径前缀过滤（绝对路径或相对索引根），仅返回文件路径以该前缀开头的符号。
    /// </param>
    IReadOnlyList<CodeSymbolMatch> Search(
        string query,
        int maxResults = 50,
        string? kindFilter = null,
        string? pathScope = null);

    /// <summary>
    /// 清空当前索引。
    /// </summary>
    void Clear();
}
