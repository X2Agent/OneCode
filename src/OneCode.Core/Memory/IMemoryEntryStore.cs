namespace OneCode.Core.Memory;

/// <summary>
/// Abstraction for structured memory entry storage.
/// </summary>
/// <remarks>
/// <para>
/// Decouples callers (MemoryService, AutoDreamService, MemoryCommand) from the physical
/// storage backend. The production implementation uses per-scope <c>MEMORY.md</c> files;
/// tests use an in-memory implementation (<c>InMemoryMemoryEntryStore</c>).
/// </para>
///
/// <para>
/// <b>Scope semantics</b>: callers specify <see cref="MemoryScope.User"/> (global,
/// cross-project) or <see cref="MemoryScope.Project"/> (current working directory). The
/// implementation resolves the physical location internally — callers never deal with
/// file paths or connection strings.
/// </para>
///
/// <para>
/// <b>Thread safety</b>: implementations must guarantee that concurrent writes to the same
/// scope are serialized (e.g. via per-scope locking). Reads are lock-free.
/// </para>
///
/// <para>
/// <b>Expiry</b>: <see cref="LoadAsync"/> filters out
/// expired entries (<see cref="MemoryEntry.IsExpired"/>). <see cref="LoadAllAsync"/>
/// includes them (for management commands). <see cref="PruneAsync"/> physically removes
/// expired entries and enforces capacity limits (LRU eviction).
/// </para>
/// </remarks>
public interface IMemoryEntryStore
{
    /// <summary>
    /// Loads all non-expired entries for the given scope.
    /// </summary>
    /// <param name="scope">User (global) or Project (current working directory).</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<MemoryEntry>> LoadAsync(MemoryScope scope, CancellationToken ct = default);

    /// <summary>
    /// Loads all entries for the given scope, including expired ones.
    /// Used by management commands (e.g. <c>/memory list</c>) that need to show all entries.
    /// </summary>
    Task<IReadOnlyList<MemoryEntry>> LoadAllAsync(MemoryScope scope, CancellationToken ct = default);

    /// <summary>
    /// Atomically upserts entries. Existing entries with the same key are overwritten
    /// (preserving original <see cref="MemoryEntry.CreatedAt"/>); new entries are appended.
    /// Expired entries are NOT pruned here — call <see cref="PruneAsync"/> for that.
    /// </summary>
    Task UpsertAsync(MemoryScope scope, IEnumerable<MemoryEntry> entries, CancellationToken ct = default);

    /// <summary>
    /// Removes the entry with the specified key.
    /// </summary>
    /// <returns><see langword="true"/> if an entry was removed; <see langword="false"/> if the key was not found.</returns>
    Task<bool> RemoveAsync(MemoryScope scope, string key, CancellationToken ct = default);

    /// <summary>
    /// Clears all entries for the given scope (deletes the backing store for that scope).
    /// </summary>
    Task ClearAsync(MemoryScope scope, CancellationToken ct = default);

    /// <summary>
    /// Removes all expired entries and enforces the capacity limit via LRU eviction
    /// (oldest <see cref="MemoryEntry.UpdatedAt"/> first).
    /// </summary>
    /// <returns>The number of entries removed (expired + LRU-evicted).</returns>
    Task<int> PruneAsync(MemoryScope scope, CancellationToken ct = default);
}
