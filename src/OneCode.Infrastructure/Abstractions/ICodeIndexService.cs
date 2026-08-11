namespace OneCode.Infrastructure.Abstractions;

/// <summary>
/// Represents a code symbol found during indexing.
/// </summary>
/// <param name="Name">Symbol name (class name, method name, etc.).</param>
/// <param name="Kind">Kind of symbol (class, method, interface, enum, etc.).</param>
/// <param name="FilePath">Absolute path to the file containing this symbol.</param>
/// <param name="Line">1-based line number where the symbol is declared.</param>
/// <param name="Column">1-based column where the symbol starts.</param>
public sealed record CodeSymbol(
    string Name,
    string Kind,
    string FilePath,
    int Line,
    int Column);

/// <summary>
/// Search result for a symbol lookup.
/// </summary>
/// <param name="Symbol">The matched symbol.</param>
/// <param name="RelevanceScore">Relevance score (1.0 = exact match).</param>
public sealed record CodeSymbolMatch(CodeSymbol Symbol, double RelevanceScore);

/// <summary>
/// Code symbol indexing service — scans source files and builds
/// an inverted index for fast symbol lookups.
/// </summary>
public interface ICodeIndexService
{
    /// <summary>Whether the index is currently being built.</summary>
    bool IsIndexing { get; }

    /// <summary>Total number of indexed symbols.</summary>
    int SymbolCount { get; }

    /// <summary>Last indexing completion time, or null if never indexed.</summary>
    DateTimeOffset? LastIndexedAt { get; }

    /// <summary>
    /// Build or rebuild the full index for the given root directory.
    /// </summary>
    Task BuildIndexAsync(string rootDirectory, CancellationToken ct = default);

    /// <summary>
    /// Update the index for changed files (add, modify, remove).
    /// </summary>
    Task UpdateFilesAsync(IEnumerable<string> changedFiles, IEnumerable<string>? removedFiles = null, CancellationToken ct = default);

    /// <summary>
    /// Search for symbols matching the given query.
    /// Returns results ordered by relevance (exact match > prefix match > substring match).
    /// </summary>
    /// <param name="query">Symbol name to search (partial match supported).</param>
    /// <param name="maxResults">Maximum number of results to return.</param>
    /// <param name="kindFilter">
    /// Optional symbol kind filter (e.g. "class", "method", "interface", "enum", "function").
    /// Case-insensitive. When null, all kinds are returned.
    /// </param>
    /// <param name="pathScope">
    /// Optional directory or file path prefix filter (absolute or relative to the indexed root).
    /// Only symbols whose file path starts with this prefix are returned.
    /// </param>
    IReadOnlyList<CodeSymbolMatch> Search(
        string query,
        int maxResults = 50,
        string? kindFilter = null,
        string? pathScope = null);

    /// <summary>
    /// Clear the current index.
    /// </summary>
    void Clear();
}
