using OneCode.Infrastructure;

namespace OneCode.App.Services;

/// <summary>
/// 增量审查缓存：基于 commit hash 标记已审查的提交。
/// 避免每次 /review 都全量审查相同的代码。
///
/// 存储位置: ~/.onecode/review-cache/{baseRef}.json
/// 每个文件记录: { commitHash → 审查时间 }
/// </summary>
public sealed class ReviewCache
{
    private static readonly string CacheDir = Path.Combine(
        PathsHelper.GetUserConfigDir(), "review-cache");

    private readonly Dictionary<string, DateTimeOffset> _reviewed = new(StringComparer.OrdinalIgnoreCase);

    public int Count => _reviewed.Count;

    /// <summary>
    /// 加载指定 baseRef 的缓存。
    /// </summary>
    /// <param name="baseRef">Git base reference (branch/commit). Defaults to HEAD when null.</param>
    /// <param name="logger">可选日志器，缺失时回退到 <see cref="System.Diagnostics.Debug"/>。</param>
    public static ReviewCache Load(string? baseRef, ILogger? logger = null)
    {
        var cache = new ReviewCache();
        var key = PathsHelper.SanitizeFileName(baseRef ?? "HEAD");
        var path = Path.Combine(CacheDir, $"{key}.json");

        try
        {
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                var entries = JsonSerializer.Deserialize<Dictionary<string, DateTimeOffset>>(json);
                if (entries != null)
                {
                    foreach (var (hash, time) in entries)
                        cache._reviewed[hash] = time;
                }
            }
        }
        catch (Exception ex)
        {
            // 缓存损坏，从空开始 — 按 §5.1 仍需记录日志
            if (logger is not null)
                logger.LogDebug(ex, "Review cache load failed for {BaseRef}, starting fresh", baseRef ?? "HEAD");
            else
                System.Diagnostics.Debug.WriteLine($"Review cache load failed for {baseRef ?? "HEAD"}: {ex.Message}");
        }

        return cache;
    }

    /// <summary>
    /// 检查 commit 是否已审查。
    /// </summary>
    public bool IsReviewed(string commitHash)
    {
        return _reviewed.ContainsKey(commitHash);
    }

    /// <summary>
    /// 从 commit 列表中过滤出未审查的。
    /// </summary>
    public IEnumerable<string> FilterNewCommits(IEnumerable<string> commitHashes)
    {
        return commitHashes.Where(h => !_reviewed.ContainsKey(h));
    }

    /// <summary>
    /// 标记 commit 为已审查。
    /// </summary>
    public void MarkReviewed(string commitHash)
    {
        _reviewed[commitHash] = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// 批量标记已审查。
    /// </summary>
    public void MarkReviewed(IEnumerable<string> commitHashes)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var hash in commitHashes)
            _reviewed[hash] = now;
    }

    /// <summary>
    /// 持久化到磁盘。
    /// </summary>
    /// <param name="baseRef">Git base reference (branch/commit). Defaults to HEAD when null.</param>
    /// <param name="logger">可选日志器，缺失时回退到 <see cref="System.Diagnostics.Debug"/>。</param>
    public void Save(string? baseRef, ILogger? logger = null)
    {
        try
        {
            Directory.CreateDirectory(CacheDir);
            var key = PathsHelper.SanitizeFileName(baseRef ?? "HEAD");
            var path = Path.Combine(CacheDir, $"{key}.json");
            var json = JsonSerializer.Serialize(_reviewed);
            File.WriteAllText(path, json);
        }
        catch (Exception ex)
        {
            // 写入失败不阻塞审查流程 — 按 §5.1 仍需记录日志
            if (logger is not null)
                logger.LogDebug(ex, "Review cache save failed for {BaseRef}", baseRef ?? "HEAD");
            else
                System.Diagnostics.Debug.WriteLine($"Review cache save failed for {baseRef ?? "HEAD"}: {ex.Message}");
        }
    }
}
