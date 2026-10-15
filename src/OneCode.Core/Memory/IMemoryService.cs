namespace OneCode.Core.Memory;

/// <summary>
/// Loads structured memory entries and builds prompt sections for the agent and management commands.
/// </summary>
public interface IMemoryService
{
    Task<string?> LoadMemoryPromptAsync(CancellationToken ct = default);

    Task<IReadOnlyList<MemoryEntryInfo>> ListMemoryEntriesAsync(CancellationToken ct = default);
}

/// <summary>A memory entry with its display index and scope, for /memory list.</summary>
public sealed record MemoryEntryInfo(
    int Index,
    MemoryEntry Entry,
    string ScopeLabel);
