namespace OneCode.Core.Memory;

/// <summary>
/// Loads structured memory entries and builds prompt / search results for the agent.
/// </summary>
public interface IMemoryService
{
    Task<string?> LoadMemoryPromptAsync(
        string workingDirectory,
        string? query = null,
        CancellationToken ct = default);

    Task<IReadOnlyList<MemoryEntryMatch>> FindRelevantMemoriesAsync(
        string workingDirectory,
        string query,
        CancellationToken ct = default);

    Task<IReadOnlyList<MemoryEntryInfo>> ListMemoryEntriesAsync(
        string workingDirectory,
        CancellationToken ct = default);
}

/// <summary>A memory entry with its scope and relevance score.</summary>
public sealed record MemoryEntryMatch(
    MemoryEntry Entry,
    MemoryScope Scope,
    int RelevanceScore);

/// <summary>A memory entry with its display index and scope, for /memory list.</summary>
public sealed record MemoryEntryInfo(
    int Index,
    MemoryEntry Entry,
    string ScopeLabel);
