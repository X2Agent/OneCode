using System.Text;

namespace OneCode.App.Tools;

/// <summary>Thread-safe LRU-ish URL cache with TTL and size-based eviction.</summary>
public sealed class WebFetchCache
{
    private const long MaxCacheSizeBytes = 50 * 1024 * 1024;

    private readonly ConcurrentDictionary<string, CacheEntry> _entries = new();
    private long _currentCacheSize;

    public bool TryGet(string url, out string content)
    {
        if (_entries.TryGetValue(url, out var entry) && entry.ExpiresAt > DateTimeOffset.UtcNow)
        {
            content = entry.Content;
            return true;
        }

        if (entry is not null)
        {
            Evict(url);
        }

        content = string.Empty;
        return false;
    }

    public void Set(string url, string content, TimeSpan ttl)
    {
        var size = Encoding.UTF8.GetByteCount(content);
        var entry = new CacheEntry(content, size, DateTimeOffset.UtcNow.Add(ttl));
        _entries[url] = entry;
        Interlocked.Add(ref _currentCacheSize, size);
        EvictIfNeeded();
    }

    public void Evict(string url)
    {
        if (_entries.TryRemove(url, out var entry))
            Interlocked.Add(ref _currentCacheSize, -entry.Size);
    }

    private void EvictIfNeeded()
    {
        if (_currentCacheSize <= MaxCacheSizeBytes)
            return;

        var oldest = _entries.OrderBy(kvp => kvp.Value.ExpiresAt).Take(_entries.Count / 2).ToList();
        foreach (var kvp in oldest)
        {
            Evict(kvp.Key);
            if (_currentCacheSize <= MaxCacheSizeBytes / 2)
                break;
        }
    }

    private sealed record CacheEntry(string Content, int Size, DateTimeOffset ExpiresAt);
}
