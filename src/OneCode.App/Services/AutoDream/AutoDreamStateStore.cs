namespace OneCode.App.Services.AutoDream;

/// <summary>
/// AutoDream 运行时状态存储：管理跨进程整合文件锁（<c>autodream.lock</c>）。
/// 状态目录由 <see cref="AutoDreamService.GetProjectStateDir"/> 动态提供（project 优先，回退全局目录）。
/// </summary>
internal sealed class AutoDreamStateStore(ILogger logger, Func<string> stateDirProvider)
{
    private const string ConsolidationLockFile = "autodream.lock";

    /// <summary>整合锁文件最大存活时间：超过则视为僵尸锁，可安全抢占。</summary>
    private static readonly TimeSpan StaleLockTimeout = TimeSpan.FromHours(2);

    private readonly ILogger _logger = logger;
    private readonly Func<string> _stateDirProvider = stateDirProvider;

    /// <summary>
    /// 原子地获取跨进程整合锁。
    /// 使用 FileStream + FileShare.None，多进程同时调用时仅一个成功。
    /// 僵尸锁（超 2 小时）可安全抢占（最多重试一次）。返回的 FileStream 持有锁，Dispose 即释放。
    /// </summary>
    public FileStream? TryAcquireConsolidationLock()
    {
        var lockPath = GetFilePath(ConsolidationLockFile);
        EnsureDir();

        for (var attempt = 0; attempt < 2; attempt++)
        {
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
            catch (IOException) when (attempt == 0)
            {
                // 文件被其他进程独占——检查是否为僵尸锁，若是则删除后进入下一次循环抢占
                if (!TryClearStaleLock(lockPath))
                    return null;
            }
            catch (IOException)
            {
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to acquire consolidation lock at {LockPath}", lockPath);
                return null;
            }
        }

        return null;
    }

    private bool TryClearStaleLock(string lockPath)
    {
        try
        {
            var content = File.ReadAllText(lockPath).Trim();
            if (DateTimeOffset.TryParse(content, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out var lockTime)
                && DateTimeOffset.UtcNow - lockTime > StaleLockTimeout)
            {
                _logger.LogWarning("AutoDream stale lock (age {Age:F1}h), forcing takeover",
                    (DateTimeOffset.UtcNow - lockTime).TotalHours);
                File.Delete(lockPath);
                return true;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to inspect stale lock at {LockPath}", lockPath);
        }

        return false;
    }

    private string GetFilePath(string fileName) => Path.Combine(_stateDirProvider(), fileName);

    private void EnsureDir()
    {
        var dir = _stateDirProvider();
        if (!Directory.Exists(dir))
            Directory.CreateDirectory(dir);
    }
}
