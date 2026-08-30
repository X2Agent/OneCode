namespace OneCode.App.Services.AutoDream;

/// <summary>
/// AutoDream 运行时状态文件存储：时间戳（last_consolidated_at / last_session_scan_at）
/// 与跨进程整合锁（autodream.lock）。状态目录由 <see cref="AutoDreamService.GetProjectStateDir"/>
/// 动态提供（project 优先，回退全局目录）。
/// </summary>
internal sealed class AutoDreamStateStore
{
    private const string ConsolidationLockFile = "autodream.lock";
    private const string LastConsolidatedAtFile = "last_consolidated_at";
    private const string LastSessionScanAtFile = "last_session_scan_at";

    /// <summary>整合锁文件最大存活时间：超过则视为僵尸锁，可安全抢占。</summary>
    private static readonly TimeSpan StaleLockTimeout = TimeSpan.FromHours(2);

    private readonly ILogger _logger;
    private readonly Func<string> _stateDirProvider;

    public AutoDreamStateStore(ILogger logger, Func<string> stateDirProvider)
    {
        _logger = logger;
        _stateDirProvider = stateDirProvider;
    }

    public DateTimeOffset GetLastConsolidatedAt() => Read(LastConsolidatedAtFile);

    public void SetLastConsolidatedAt(DateTimeOffset time) =>
        Write(LastConsolidatedAtFile, time.ToString("O", CultureInfo.InvariantCulture));

    public DateTimeOffset GetLastSessionScanAt() => Read(LastSessionScanAtFile);

    public void SetLastSessionScanAt(DateTimeOffset time) =>
        Write(LastSessionScanAtFile, time.ToString("O", CultureInfo.InvariantCulture));

    /// <summary>
    /// 原子地获取跨进程整合锁。
    /// 使用 FileStream + FileShare.None，多进程同时调用时仅一个成功。
    /// 僵尸锁（超 2 小时）可安全抢占。返回的 FileStream 持有锁，Dispose 即释放。
    /// </summary>
    public FileStream? TryAcquireConsolidationLock()
    {
        var lockPath = GetFilePath(ConsolidationLockFile);
        EnsureDir();

        try
        {
            var stream = new FileStream(lockPath, FileMode.Create, FileAccess.Write, FileShare.None);
            using (var writer = new StreamWriter(stream, leaveOpen: true))
            {
                writer.Write(DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
                writer.Flush();
            }
            stream.Seek(0, SeekOrigin.Begin);
            return stream;
        }
        catch (IOException)
        {
            // 文件被其他进程独占——检查是否为僵尸锁
            try
            {
                var content = File.ReadAllText(lockPath).Trim();
                if (DateTimeOffset.TryParse(content, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal, out var lockTime))
                {
                    if (DateTimeOffset.UtcNow - lockTime > StaleLockTimeout)
                    {
                        _logger.LogWarning("AutoDream stale lock (age {Age:F1}h), forcing takeover",
                            (DateTimeOffset.UtcNow - lockTime).TotalHours);
                        File.Delete(lockPath);
                        return TryAcquireConsolidationLock();
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to inspect stale lock at {LockPath}", lockPath);
            }
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to acquire consolidation lock at {LockPath}", lockPath);
            return null;
        }
    }

    private string GetFilePath(string fileName) => Path.Combine(_stateDirProvider(), fileName);

    private void EnsureDir()
    {
        var dir = _stateDirProvider();
        if (!Directory.Exists(dir))
            Directory.CreateDirectory(dir);
    }

    private DateTimeOffset Read(string fileName)
    {
        var filePath = GetFilePath(fileName);
        if (!File.Exists(filePath)) return DateTimeOffset.MinValue;
        try
        {
            var text = File.ReadAllText(filePath).Trim();
            return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out var dt) ? dt : DateTimeOffset.MinValue;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read state from {FilePath}", filePath);
            return DateTimeOffset.MinValue;
        }
    }

    private void Write(string fileName, string content)
    {
        var filePath = GetFilePath(fileName);
        try
        {
            EnsureDir();
            File.WriteAllText(filePath, content);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to write state file {FilePath}", filePath);
        }
    }
}
