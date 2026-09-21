namespace OneCode.Core.Memory;

/// <summary>
/// Thrown when the backing store exists but could not be read, so its current contents are
/// <b>unknown</b>.
/// </summary>
/// <remarks>
/// <para>
/// This type exists to separate "the store is empty" from "the store could not be read". Before it,
/// a read failure was reported as an empty entry list, which made a read-modify-write operation
/// (upsert / remove / record hits / prune) treat the store as empty and overwrite the previous
/// contents with only the new entries — silent data loss on a transient I/O error.
/// </para>
/// <para>
/// <b>Callers must not swallow this into an empty result.</b> Read-only query paths may degrade to
/// an empty list, but mutation paths must propagate so the write is refused rather than applied on
/// top of an unknown baseline.
/// </para>
/// <para>
/// Cancellation is <b>not</b> wrapped in this type: <see cref="OperationCanceledException"/> keeps
/// flowing unchanged so callers can distinguish a user cancel from a broken store.
/// </para>
/// </remarks>
public sealed class MemoryStoreReadException : Exception
{
    /// <summary>The scope whose backing store could not be read.</summary>
    public MemoryScope Scope { get; }

    /// <summary>The path that could not be read, when the backend has one.</summary>
    /// <remarks>Carried for logging only. Callers must not surface it to the model as a result.</remarks>
    public string? FilePath { get; init; }

    public MemoryStoreReadException(MemoryScope scope, string message, Exception innerException)
        : base(message, innerException)
    {
        Scope = scope;
    }
}
