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
/// expired entries and enforces capacity limits (usage-ranked eviction, see
/// <see cref="PruneAsync"/>).
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
    /// Records usage feedback for the specified entries: increments
    /// <see cref="MemoryEntry.HitCount"/> and stamps <see cref="MemoryEntry.LastHitAt"/>.
    /// </summary>
    /// <param name="scope">User (global) or Project (current working directory).</param>
    /// <param name="keys">
    /// Keys that were actually recalled. Unknown keys are ignored (not an error) so callers can
    /// pass a whole result set without pre-filtering.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <remarks>
    /// <para>
    /// <b>Contract — <see cref="MemoryEntry.UpdatedAt"/> must NOT be touched.</b> Usage is not a
    /// content change; bumping the timestamp would let a frequently-recalled entry masquerade as
    /// "fresh" and escape the <see cref="MemoryEntry.UpdatedAt"/> tie-break in
    /// <see cref="PruneAsync"/>. The same applies to <see cref="MemoryEntry.CreatedAt"/> and
    /// <see cref="MemoryEntry.ExpiresAt"/>.
    /// </para>
    /// <para>
    /// Callers treat this as best-effort: a failure here must not fail the tool call that produced
    /// the hits, so implementations should not throw for I/O problems.
    /// </para>
    /// </remarks>
    Task RecordHitsAsync(MemoryScope scope, IReadOnlyList<string> keys, CancellationToken ct = default);

    /// <summary>
    /// Clears all entries for the given scope (deletes the backing store for that scope).
    /// </summary>
    Task ClearAsync(MemoryScope scope, CancellationToken ct = default);

    /// <summary>
    /// Removes all expired entries and enforces the capacity limit by evicting the least valuable
    /// entries (retention order below).
    /// </summary>
    /// <param name="scope">User (global) or Project (current working directory).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The number of entries removed (expired + evicted).</returns>
    /// <remarks>
    /// Eviction order when over the capacity limit, least valuable first:
    /// <list type="number">
    /// <item><see cref="MemoryEntry.Source"/> <c>manual</c> entries are <b>exempt</b> — they express
    ///   explicit user intent and are never evicted automatically.</item>
    /// <item>Everything else by <see cref="MemoryEntry.HitCount"/> ascending — never-recalled
    ///   entries go before well-used ones.</item>
    /// <item>Ties broken by <see cref="MemoryEntry.UpdatedAt"/> ascending — the oldest goes first.
    ///   This is why <see cref="RecordHitsAsync"/> must not bump that timestamp.</item>
    /// </list>
    /// </remarks>
    Task<int> PruneAsync(MemoryScope scope, CancellationToken ct = default);
}
