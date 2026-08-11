namespace OneCode.App.Services.Cache;

public sealed class FileContentCache : IFileContentCache
{
    public const string FileUnchangedStub =
        "File unchanged since last read. The content from the earlier Read tool_result in this conversation is still current — refer to that instead of re-reading.";

    private readonly Dictionary<string, FileState> _states = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    public FileState? Get(string filePath)
    {
        lock (_lock)
        {
            if (!_states.TryGetValue(filePath, out var state))
                return null;

            if (!File.Exists(filePath))
            {
                _states.Remove(filePath);
                return null;
            }

            var currentMtime = File.GetLastWriteTimeUtc(filePath);
            if (currentMtime != state.Timestamp)
            {
                _states.Remove(filePath);
                return null;
            }

            return state;
        }
    }

    public bool TryDedupRead(string filePath, int offset, int limit)
    {
        lock (_lock)
        {
            var state = Get(filePath);
            if (state == null)
                return false;

            if (state.IsPartialView)
                return false;

            if (state.Offset == null)
                return false;

            if (state.Offset != offset || state.Limit != limit)
                return false;

            return true;
        }
    }

    public void SetAfterRead(string filePath, string content, int? offset, int? limit)
    {
        lock (_lock)
        {
            var mtime = File.Exists(filePath) ? File.GetLastWriteTimeUtc(filePath) : DateTime.MinValue;
            _states[filePath] = new FileState(content, mtime, offset, limit, IsPartialView: limit != null);
        }
    }

    public void SetAfterWrite(string filePath, string content)
    {
        lock (_lock)
        {
            var mtime = File.Exists(filePath) ? File.GetLastWriteTimeUtc(filePath) : DateTime.MinValue;
            _states[filePath] = new FileState(content, mtime, Offset: null, Limit: null, IsPartialView: false);
        }
    }

    public void Invalidate(string filePath)
    {
        lock (_lock)
        {
            _states.Remove(filePath);
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _states.Clear();
        }
    }

    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _states.Count;
            }
        }
    }
}

public sealed record FileState(
    string Content,
    DateTime Timestamp,
    int? Offset,
    int? Limit,
    bool IsPartialView);
