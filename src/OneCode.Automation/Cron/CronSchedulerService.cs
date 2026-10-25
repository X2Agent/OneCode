using System.Text.Json;
using OneCode.Core.Cron;

namespace OneCode.Automation.Cron;

/// <summary>
/// Background service that polls <c>~/{ConfigDir}/cron/*.json</c> and fires due
/// <see cref="CronJobEntry"/> jobs by delegating their prompt to <see cref="ICronJobExecutor"/>
/// (implemented by the App layer and reverse-injected via DI).
///
/// Jobs live as long as the host process is running, unless a job is marked <see cref="CronJobEntry.Durable"/>,
/// in which case it persists to <c>~/{ConfigDir}/cron/{Id}.json</c> and survives restarts.
/// </summary>
/// <remarks>
/// The scheduling core (parsing, persistence, filesystem watching, polling loop) lives in
/// <c>OneCode.Automation</c> while the conversation-side execution stays in App via
/// <see cref="ICronJobExecutor"/>.
/// </remarks>
public sealed class CronSchedulerService : BackgroundService
{
    private static readonly TimeSpan ReloadDebounce = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);
    private static readonly JsonSerializerOptions s_jsonOptions = new() { WriteIndented = true };

    private readonly ILogger<CronSchedulerService> _logger;
    private readonly ICronParser _cronParser;
    private readonly ICronJobExecutor _executor;
    private readonly string _cronDir;
    private readonly string? _projectDir;
    private readonly FileSystemWatcher? _watcher;
    private readonly object _reloadLock = new();
    private readonly HashSet<string> _pendingJobIds = new(StringComparer.OrdinalIgnoreCase);
    private List<CronJobEntry> _jobs = [];
    private DateTimeOffset _lastReload = DateTimeOffset.MinValue;

    /// <summary>Maximum number of active and pending cron jobs in one scheduler.</summary>
    public const int MaxJobs = 50;
    private bool _disposed;

    /// <summary>
    /// <paramref name="projectDir"/> 为本调度器服务的项目目录（进程工作目录），durable 作业按此过滤触发；
    /// <paramref name="cronDir"/> 覆盖 cron 作业目录，测试注入临时目录用，缺省为全局 <c>~/{ConfigDir}/cron</c>。
    /// </summary>
    public CronSchedulerService(
        ILogger<CronSchedulerService> logger,
        ICronParser cronParser,
        ICronJobExecutor executor,
        string? projectDir = null,
        string? cronDir = null)
    {
        _logger = logger;
        _cronParser = cronParser;
        _executor = executor;
        _cronDir = cronDir ?? CronPaths.GetCronDirectory();
        _projectDir = projectDir is null ? null : Path.GetFullPath(projectDir);

        // Always create the directory so the watcher can observe it. If the directory
        // doesn't exist at startup, FileSystemWatcher silently does nothing — durable
        // jobs created later via CronTool would never trigger ReloadJobs.
        Directory.CreateDirectory(_cronDir);

        _watcher = new FileSystemWatcher(_cronDir, "*.json")
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
            EnableRaisingEvents = true,
        };
        _watcher.Created += (_, _) => ReloadJobs();
        _watcher.Deleted += (_, _) => ReloadJobs();
        _watcher.Changed += (_, _) => ReloadJobs();
        _watcher.Renamed += (_, _) => ReloadJobs();
    }

    public IReadOnlyList<CronJobEntry> GetJobs()
    {
        // Snapshot under the lock: callers (CronTool, CronCommand) iterate the
        // returned collection, and concurrent ReloadJobs / AddJob / TryRemoveJob
        // mutations would otherwise throw InvalidOperationException on List<T>
        // enumeration. ToArray() takes a copy under the lock, which IS safe.
        lock (_reloadLock)
        {
            return _jobs.ToArray();
        }
    }

    /// <summary>
    /// Atomically reserves capacity and adds a cron job. Durable jobs are persisted before
    /// becoming visible; non-durable jobs are added directly to memory. The reservation is
    /// counted while persistence is in flight, so concurrent callers can never exceed
    /// <see cref="MaxJobs"/>.
    /// </summary>
    public async Task<bool> TryAddJobAsync(CronJobEntry entry, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (!CronPaths.IsValidJobId(entry.Id))
            return false;

        // 归属盖章：durable 作业跨进程共享磁盘，ProjectDir 决定哪个项目的进程可触发它。
        entry.ProjectDir ??= _projectDir;

        lock (_reloadLock)
        {
            if (_jobs.Count + _pendingJobIds.Count >= MaxJobs)
                return false;
            if (_jobs.Any(j => string.Equals(j.Id, entry.Id, StringComparison.OrdinalIgnoreCase)) ||
                !_pendingJobIds.Add(entry.Id))
                return false;
        }

        var persisted = false;
        try
        {
            if (entry.Durable)
            {
                await PersistJobCoreAsync(entry, ct).ConfigureAwait(false);
                persisted = true;
            }

            lock (_reloadLock)
            {
                var existing = _jobs.FirstOrDefault(j =>
                    string.Equals(j.Id, entry.Id, StringComparison.OrdinalIgnoreCase));
                if (existing is not null)
                {
                    // The file watcher may have loaded this durable job between persistence
                    // and lock reacquisition. That is the same successful creation, not a duplicate.
                    return entry.Durable;
                }
                if (_jobs.Count >= MaxJobs)
                    return false;
                _jobs.Add(entry);
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to add cron job {Id}", entry.Id);
            return false;
        }
        finally
        {
            lock (_reloadLock)
                _pendingJobIds.Remove(entry.Id);

            if (persisted)
            {
                lock (_reloadLock)
                {
                    if (_jobs.Any(j => string.Equals(j.Id, entry.Id, StringComparison.OrdinalIgnoreCase)))
                        persisted = false;
                }
                if (persisted)
                {
                    try
                    {
                        var path = CronPaths.GetJobFilePath(entry.Id);
                        if (File.Exists(path)) File.Delete(path);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to roll back cron job file {Id}", entry.Id);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Remove a job from the in-memory list AND delete its durable JSON file (if any).
    /// </summary>
    /// <returns>
    /// <see langword="true"/> when the job was fully removed (in-memory + file deleted or absent).
    /// <see langword="false"/> when the job was not found OR when the durable file could not be
    /// deleted — in the latter case the job is gone from memory but may revive on the next
    /// <see cref="ReloadJobs"/> because the orphaned file is re-loaded from disk.
    /// </returns>
    public bool TryRemoveJob(string id)
    {
        if (!CronPaths.IsValidJobId(id)) return false;

        lock (_reloadLock)
        {
            var idx = _jobs.FindIndex(j => string.Equals(j.Id, id, StringComparison.OrdinalIgnoreCase));
            if (idx < 0) return false;
            _jobs.RemoveAt(idx);
        }

        try
        {
            var path = CronPaths.GetJobFilePath(id);
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            // File deletion failed: the job is gone from memory, but the orphaned JSON
            // will be re-loaded by ReloadJobs (FileSystemWatcher or next poll), causing
            // the job to "revive". Signal partial failure so the caller can retry or
            // warn the user.
            _logger.LogWarning(ex,
                "Failed to delete cron job file {Id}; job removed from memory but may revive on reload", id);
            return false;
        }

        return true;
    }

    /// <summary>
    /// Set <see cref="CronJobEntry.Paused"/> on a job and persist the change (if durable).
    /// Returns <c>false</c> if the job was not found.
    /// </summary>
    public async Task<bool> TrySetPausedAsync(string id, bool paused, CancellationToken ct = default)
    {
        CronJobEntry? job;
        lock (_reloadLock)
        {
            job = _jobs.FirstOrDefault(j => string.Equals(j.Id, id, StringComparison.OrdinalIgnoreCase));
            if (job is null) return false;
            job.Paused = paused;

            // On resume, recompute NextRunAt from "now + 1s" so the job doesn't fire a backlog
            // of missed occurrences while paused.
            if (!paused && job.Recurring)
            {
                var next = _cronParser.ComputeNextRun(job.Cron, DateTimeOffset.UtcNow.AddSeconds(1));
                job.NextRunAt = next?.ToUnixTimeSeconds();
            }
        }

        if (job.Durable)
        {
            // Await (not fire-and-forget) so that callers observe persistence failures and
            // the host doesn't exit before the file write completes.
            await PersistJobAsync(job, ct).ConfigureAwait(false);
        }

        return true;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Cron scheduler service started");

        await ReloadJobsAsync(stoppingToken).ConfigureAwait(false);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(PollInterval, stoppingToken).ConfigureAwait(false);
                await CheckAndFireJobsAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in cron scheduler loop");
            }
        }
    }

    public override void Dispose()
    {
        if (_disposed) return;
        // Dispose the FileSystemWatcher deterministically. Previously this only ran at the
        // end of ExecuteAsync, but if ExecuteAsync threw before reaching the end, or if
        // the host stopped the service before ExecuteAsync started, _watcher would leak
        // and keep firing events on a disposed container.
        _watcher?.Dispose();
        _disposed = true;
        base.Dispose();
    }

    /// <summary>
    /// 检查快照中到期的作业并触发。durable 作业触发前先取跨进程独占锁，
    /// 被其他进程（同项目 TUI/daemon）持有时跳过本轮。
    /// </summary>
    internal async Task CheckAndFireJobsAsync(CancellationToken ct)
    {
        // Snapshot under the lock so we iterate a stable copy. AddJob / ReloadJobs /
        // TryRemoveJob / TrySetPaused may mutate _jobs concurrently from the
        // FileSystemWatcher thread or the TUI thread; iterating _jobs directly
        // here would race with those mutations (lost writes, InvalidOperation
        // exceptions on List<T> enumeration, etc.).
        List<CronJobEntry> snapshot;
        lock (_reloadLock)
        {
            snapshot = _jobs.ToList();
        }

        if (snapshot.Count == 0) return;

        var now = DateTimeOffset.UtcNow;

        foreach (var job in snapshot)
        {
            if (job.Paused) continue;
            if (job.NextRunAt is null) continue;
            var nextRun = DateTimeOffset.FromUnixTimeSeconds(job.NextRunAt.Value);

            if (now < nextRun) continue;

            FileStream? fireLock = null;
            if (job.Durable)
            {
                fireLock = TryAcquireFireLock(job.Id);
                if (fireLock is null)
                {
                    // 另一进程正在触发本作业：跳过。触发方落盘新 NextRunAt 后经
                    // FileSystemWatcher 重载回本进程内存，后续轮询自然不再重试。
                    _logger.LogInformation(
                        "Skipping cron job {Id}: fire lock held by another process", job.Id);
                    continue;
                }
            }

            try
            {
                await FireJobAsync(job, now, ct).ConfigureAwait(false);
            }
            finally
            {
                fireLock?.Dispose();
            }
        }
    }

    private async Task FireJobAsync(CronJobEntry job, DateTimeOffset now, CancellationToken ct)
    {
        _logger.LogInformation("Firing cron job {Id}: {Prompt}", job.Id, job.Prompt);

        string lastStatus = "ok";
        string? lastError = null;
        try
        {
            await _executor.ExecuteJobAsync(job.Prompt, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Host is shutting down — propagate so ExecuteAsync's loop breaks.
            throw;
        }
        catch (Exception ex)
        {
            lastStatus = "error";
            lastError = ex.Message;
            _logger.LogError(ex, "Error triggering cron job {Id}", job.Id);
        }

        // Update job state under the lock. We hold the lock briefly (no awaits
        // inside) so that concurrent mutation from TrySetPaused / TryRemoveJob
        // is serialized — without this, two writers could clobber each other's
        // updates to NextRunAt / LastRunAt / the in-memory list itself.
        var isOneShot = false;
        var durableId = job.Durable ? job.Id : null;
        lock (_reloadLock)
        {
            job.LastRunAt = now.ToUnixTimeSeconds();
            job.LastStatus = lastStatus;
            job.LastError = lastError;

            if (job.Recurring)
            {
                var next = _cronParser.ComputeNextRun(job.Cron, now.AddSeconds(1));
                job.NextRunAt = next?.ToUnixTimeSeconds();
            }
            else
            {
                job.NextRunAt = null;
                _jobs.Remove(job);
                isOneShot = true;
            }
        }

        if (durableId is not null)
        {
            if (isOneShot)
            {
                // One-shot job completed: delete the JSON file so it doesn't
                // linger on disk and get re-loaded as a dead entry on restart.
                try
                {
                    var path = CronPaths.GetJobFilePath(durableId);
                    if (File.Exists(path)) File.Delete(path);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "Failed to delete completed one-shot cron file {Id}", durableId);
                }
            }
            else
            {
                await PersistJobAsync(job, ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// 尝试独占打开作业触发锁（<c>{cronDir}/locks/{id}.lock</c>）。拿不到说明另一进程
    /// （同项目 TUI/daemon）正在触发，返回 null。条件只看 <see cref="CronJobEntry.Durable"/>：
    /// durable 文件在进程间共享，多进程并存时须以锁仲裁。
    /// 锁文件不删除复用：释放后立即删除可能恰逢另一进程刚打开同名锁，破坏其独占语义；
    /// 每作业一个空文件、句柄关闭即失效，进程崩溃由 OS 回收句柄自愈。
    /// </summary>
    private FileStream? TryAcquireFireLock(string id)
    {
        var lockDir = Path.Combine(_cronDir, "locks");
        try
        {
            Directory.CreateDirectory(lockDir);
            return new FileStream(
                Path.Combine(lockDir, $"{id}.lock"),
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void ReloadJobs()
    {
        _ = Task.Run(() => ReloadJobsAsync(CancellationToken.None));
    }

    /// <summary>从 cron 目录磁盘重载 durable 作业（FileSystemWatcher 事件与启动时的同一入口）。</summary>
    internal async Task ReloadJobsAsync(CancellationToken ct)
    {
        lock (_reloadLock)
        {
            // Debounce inside the lock to prevent TOCTOU: if the check were outside,
            // multiple FSW threads could all pass the check before any acquires the lock.
            if (DateTimeOffset.UtcNow - _lastReload < ReloadDebounce) return;
            _lastReload = DateTimeOffset.UtcNow;
        }

        if (!Directory.Exists(_cronDir)) return;

        var files = Directory.GetFiles(_cronDir, "*.json");
        List<CronJobEntry> candidates = [];

        foreach (var file in files)
        {
            try
            {
                var json = await File.ReadAllTextAsync(file, ct).ConfigureAwait(false);
                var entry = JsonSerializer.Deserialize<CronJobEntry>(json);
                if (entry is null) continue;

                if (!CronPaths.IsValidJobId(entry.Id))
                {
                    _logger.LogWarning(
                        "Skipping cron job file {File}: invalid ID '{Id}' (potential path traversal)", file, entry.Id);
                    continue;
                }

                if (!entry.Durable)
                {
                    _logger.LogDebug(
                        "Skipping non-durable cron file {File} on reload", file);
                    continue;
                }

                if (!BelongsToThisProject(entry))
                {
                    _logger.LogDebug(
                        "Skipping cron job {Id} owned by other project {ProjectDir}",
                        entry.Id, entry.ProjectDir);
                    continue;
                }

                if (entry.NextRunAt is null && entry.Recurring && !entry.Paused)
                {
                    var next = _cronParser.ComputeNextRun(entry.Cron, DateTimeOffset.UtcNow);
                    entry.NextRunAt = next?.ToUnixTimeSeconds();
                }

                candidates.Add(entry);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to parse cron job file {File}; skipping", file);
            }
        }

        lock (_reloadLock)
        {
            var inMemoryOnly = _jobs.Where(j => !j.Durable).ToList();
            List<CronJobEntry> loaded = [];

            foreach (var entry in candidates)
            {
                if (loaded.Count + inMemoryOnly.Count + _pendingJobIds.Count >= MaxJobs)
                {
                    _logger.LogWarning(
                        "Skipping cron job {Id}: scheduler limit {MaxJobs} reached", entry.Id, MaxJobs);
                    continue;
                }

                if (loaded.Any(j => string.Equals(j.Id, entry.Id, StringComparison.OrdinalIgnoreCase)) ||
                    _pendingJobIds.Contains(entry.Id))
                    continue;

                loaded.Add(entry);
            }

            _jobs = inMemoryOnly.Concat(loaded).ToList();
        }
    }

    /// <summary>
    /// durable 作业按项目归属触发：<see cref="CronJobEntry.ProjectDir"/> 为 null（旧版 JSON）
    /// 或与本调度器项目一致时触发；归属其他项目的作业不加载（一个 OneCode 进程只服务一个项目）。
    /// </summary>
    private bool BelongsToThisProject(CronJobEntry entry)
    {
        if (entry.ProjectDir is null || _projectDir is null) return true;

        return string.Equals(
            Path.GetFullPath(entry.ProjectDir),
            _projectDir,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    private async Task PersistJobAsync(CronJobEntry job, CancellationToken ct)
    {
        if (!CronPaths.IsValidJobId(job.Id))
        {
            _logger.LogWarning("Cannot persist cron job with invalid ID '{Id}'", job.Id);
            return;
        }

        try
        {
            await PersistJobCoreAsync(job, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error persisting cron job {Id}", job.Id);
        }
    }

    private static async Task PersistJobCoreAsync(CronJobEntry job, CancellationToken ct)
    {
        var path = CronPaths.GetJobFilePath(job.Id);
        var json = JsonSerializer.Serialize(job, s_jsonOptions);
        await CronPaths.WriteAtomicAsync(path, json, ct).ConfigureAwait(false);
    }
}
