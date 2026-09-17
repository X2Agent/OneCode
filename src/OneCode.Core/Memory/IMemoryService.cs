namespace OneCode.Core.Memory;

/// <summary>
/// Loads structured memory entries and builds prompt / search results for the agent.
/// </summary>
public interface IMemoryService
{
    Task<string?> LoadMemoryPromptAsync(CancellationToken ct = default);

    Task<IReadOnlyList<MemoryEntryMatch>> FindRelevantMemoriesAsync(
        string query,
        CancellationToken ct = default);

    Task<IReadOnlyList<MemoryEntryInfo>> ListMemoryEntriesAsync(CancellationToken ct = default);

    /// <summary>
    /// Records usage feedback for entries that were actually recalled by an explicit search, so
    /// retention can rank by real recall instead of age alone.
    /// </summary>
    /// <param name="scope">Scope the keys belong to.</param>
    /// <param name="keys">Recalled keys; unknown keys are ignored (see <see cref="IMemoryEntryStore.RecordHitsAsync"/>).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <remarks>
    /// Best-effort: implementations must not throw for I/O problems, and must not touch
    /// <see cref="MemoryEntry.UpdatedAt"/> (see <see cref="IMemoryEntryStore.RecordHitsAsync"/>).
    /// </remarks>
    Task RecordHitsAsync(MemoryScope scope, IReadOnlyList<string> keys, CancellationToken ct = default);
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
