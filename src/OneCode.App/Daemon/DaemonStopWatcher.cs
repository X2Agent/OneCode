namespace OneCode.App.Daemon;

/// <summary>
/// daemon 侧停止哨兵监听器：监视哨兵文件出现即触发停止回调，并消费删除哨兵。
/// 哨兵是跨进程单向下行通道（TUI 的 /daemon stop 写，daemon 消费后删除）。
/// 启动时已存在的哨兵同样触发——哨兵语义是「停止」，未消费的请求不应静默丢弃；
/// 极端场景（daemon 崩溃残留哨兵致新实例即刻退出）自愈：哨兵随之被清理，再次启动即正常。
/// </summary>
public sealed class DaemonStopWatcher : IDisposable
{
    private readonly string _stopFile;
    private readonly Action _onStopRequested;
    private readonly FileSystemWatcher _watcher;
    private int _fired;

    /// <param name="stopFile">哨兵文件完整路径（由 <see cref="DaemonLifecycle.GetStopFilePath"/> 组合）。</param>
    /// <param name="onStopRequested">停止回调（由宿主传入 StopAsync；须自捕获异常）。</param>
    public DaemonStopWatcher(string stopFile, Action onStopRequested)
    {
        _stopFile = stopFile;
        _onStopRequested = onStopRequested;

        var dir = Path.GetDirectoryName(stopFile)!;
        Directory.CreateDirectory(dir);
        _watcher = new FileSystemWatcher(dir, Path.GetFileName(stopFile))
        {
            NotifyFilter = NotifyFilters.FileName,
            EnableRaisingEvents = true,
        };
        _watcher.Created += (_, _) => FireIfFirst();

        // 订阅后补查一次：哨兵在 EnableRaisingEvents 之前落盘会错过事件。
        if (File.Exists(stopFile))
            FireIfFirst();
    }

    private void FireIfFirst()
    {
        if (Interlocked.Exchange(ref _fired, 1) == 1) return;

        try
        {
            if (File.Exists(_stopFile)) File.Delete(_stopFile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 删不掉只是残留，停止语义已发出。
        }

        _onStopRequested();
    }

    public void Dispose() => _watcher.Dispose();
}
