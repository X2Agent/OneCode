namespace OneCode.App.Services.Cache;

/// <summary>
/// Per-session file content cache used by Read/Write/Edit tools for dedup stubs.
/// </summary>
public interface IFileContentCache
{
    FileState? Get(string filePath);

    bool TryDedupRead(string filePath, int offset, int limit);

    void SetAfterRead(string filePath, string content, int? offset, int? limit);

    void SetAfterWrite(string filePath, string content);

    void Invalidate(string filePath);

    void Clear();

    int Count { get; }
}
