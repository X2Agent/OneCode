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
    public DateTimeOffset UpdatedAt { get; init; }

    /// <summary>Optional expiry. <see langword="null"/> means never expires.</summary>
    public DateTimeOffset? ExpiresAt { get; init; }

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
