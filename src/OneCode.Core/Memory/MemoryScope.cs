namespace OneCode.Core.Memory;

/// <summary>Memory scope: user-level (global, cross-project) or project-level (per working directory).</summary>
/// <remarks>
/// Scope is always determined by the caller — it is NOT derived from the entry key.
/// The physical storage location is resolved by <c>IMemoryEntryStore</c> implementations
/// based on this enum (file-based: two separate MEMORY.md files; SQLite: two tables/partitions).
/// </remarks>
public enum MemoryScope
{
    /// <summary>
    /// Global — cross-project memories. Backed by <c>~/.onecode/memory/MEMORY.md</c> in the
    /// file-based implementation. A future SQLite backend would use a global table.
    /// </summary>
    User,

    /// <summary>
    /// Project — scoped to the current working directory. Backed by
    /// <c>{cwd}/.onecode/memory/MEMORY.md</c> in the file-based implementation. A future
    /// SQLite backend would key on the project root path.
    /// </summary>
    Project,
}
