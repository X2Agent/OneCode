namespace OneCode.Core.Memory;

/// <summary>
/// A single structured memory entry stored in the memory backend.
/// </summary>
/// <remarks>
/// <para>
/// All memories — both user-added (<c>/memory add</c>) and AutoDream-extracted — share the
/// same data model regardless of the storage backend (MEMORY.md today, SQLite tomorrow).
/// </para>
/// <para>
/// <b>Key format</b>: <c>{category}:{short-id}</c>, e.g. <c>fact:build-command</c>,
/// <c>manual:oauth-dpapi</c>. The key is the stable identity — reusing a key across updates
/// overwrites the prior value.
/// </para>
/// <para>
/// <b>Usage feedback</b>: <see cref="HitCount"/> / <see cref="LastHitAt"/> are written by
/// <c>IMemoryEntryStore.RecordHitsAsync</c> when the entry is actually recalled, and read by
/// <c>PruneAsync</c> to rank retention. They are the only two fields that usage may mutate.
/// </para>
/// </remarks>
public sealed record MemoryEntry
{
    /// <summary>Stable identity key in <c>{category}:{short-id}</c> format.</summary>
    public required string Key { get; init; }

    /// <summary>Full memory content (may be multi-line).</summary>
    public required string Value { get; init; }

    /// <summary>Who created this entry: <c>manual</c> (user via /memory add) or <c>autodream</c>.</summary>
    public required string Source { get; init; }

    /// <summary>
    /// Category encoded in the key prefix: <c>manual</c>, <c>fact</c>, <c>convention</c>,
    /// <c>lesson</c>, or <c>correction</c>.
    /// </summary>
    public required string Category { get; init; }

    /// <summary>When the entry was first created (UTC).</summary>
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>When the entry was last updated (UTC).</summary>
    /// <remarks>
    /// Only content writes bump this. Usage feedback (<see cref="HitCount"/>) deliberately does
    /// <b>not</b> — otherwise a frequently-recalled entry would look "fresh" and escape pruning,
    /// which would break the <see cref="UpdatedAt"/> tie-break in the eviction order.
    /// </remarks>
    public DateTimeOffset UpdatedAt { get; init; }

    /// <summary>Optional expiry. <see langword="null"/> means never expires.</summary>
    public DateTimeOffset? ExpiresAt { get; init; }

    /// <summary>
    /// How many times this entry was returned by an explicit <c>search_memories</c> call.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Usage feedback that drives eviction: retention is ranked by hit count before falling back to
    /// age (see <c>IMemoryEntryStore.PruneAsync</c>). Passive prompt injection is intentionally
    /// <b>not</b> counted — it touches every entry on every turn, which would drown the signal.
    /// </para>
    /// <para>Missing in older <c>MEMORY.md</c> files, in which case it reads as <c>0</c>.</para>
    /// </remarks>
    public int HitCount { get; init; }

    /// <summary>When this entry was last returned by <c>search_memories</c>; <see langword="null"/> if never.</summary>
    public DateTimeOffset? LastHitAt { get; init; }

    /// <summary>True when <see cref="ExpiresAt"/> has passed.</summary>
    public bool IsExpired =>
        ExpiresAt.HasValue && DateTimeOffset.UtcNow > ExpiresAt.Value;

    /// <summary>Derives the category from the key's prefix (the part before the first <c>:</c>).</summary>
    public static string DeriveCategory(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return "manual";

        var colon = key.IndexOf(':');
        return colon <= 0 ? "manual" : key[..colon];
    }
}
