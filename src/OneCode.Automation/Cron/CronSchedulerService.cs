using System.Text.Json;
using OneCode.Core.Cron;

namespace OneCode.Automation.Cron;

/// <summary>
/// Background service that polls <c>~/{ConfigDir}/cron/*.json</c> and fires due
/// <see cref="CronJobEntry"/> jobs by delegating their prompt to <see cref="ICronJobExecutor"/>
/// (implemented by the App layer and reverse-injected via DI).
///
/// Jobs live as long as the host process is running, unless a job is marked <see cref="CronJobEntry.Durable"/>
/// AND the host opted in via <c>ONECODE_DURABLE_CRON=true</c>.
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
    private readonly FileSystemWatcher? _watcher;
    private readonly object _reloadLock = new();
    private readonly HashSet<string> _pendingJobIds = new(StringComparer.OrdinalIgnoreCase);
    private List<CronJobEntry> _jobs = [];
    private DateTimeOffset _lastReload = DateTimeOffset.MinValue;

    /// <summary>Maximum number of active and pending cron jobs in one scheduler.</summary>
    public const int MaxJobs = 50;
    private bool _disposed;

    public CronSchedulerService(
        ILogger<CronSchedulerService> logger,
        ICronParser cronParser,
        ICronJobExecutor executor)
    {
        _logger = logger;
        _cronParser = cronParser;
        _executor = executor;
        _cronDir = CronPaths.GetCronDirectory();

        // Always create the directory so the watcher can observe it. If the directory
        // doesn't exist at startup, FileSystemWatcher silently does nothing — durable
        // jobs created later via CronCreateTool would never trigger ReloadJobs.
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
        // Snapshot under the lock: callers (CronListTool, CronCommand) iterate the
        // returned collection, and concurrent ReloadJobs / AddJob / TryRemoveJob
        // mutations would otherwise throw InvalidOperationException on List<T>
        // enumeration. AsReadOnly() wraps the live list, not a copy, so it is NOT
        // safe against concurrent mutation either — we must ToList() under the lock.
        lock (_reloadLock)
        {
            return _jobs.ToList().AsReadOnly();
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
                if (!CronPaths.IsDurableCronEnabled())
                    return false;
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

        if (job.Durable && CronPaths.IsDurableCronEnabled())
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

        ReloadJobs();

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

    private async Task CheckAndFireJobsAsync(CancellationToken ct)
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
            if (job.NextRunAt == null) continue;
            var nextRun = DateTimeOffset.FromUnixTimeSeconds(job.NextRunAt.Value);

            if (now >= nextRun)
            {
                _logger.LogInformation("Firing cron job {Id}: {Prompt}", job.Id, job.Prompt);

                try
                {
                    await _executor.ExecuteJobAsync(job.Prompt, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Host is shutting down — propagate so ExecuteAsync's loop breaks.
                    throw;
                }
                catch (Exception ex)
                {
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

                if (durableId is not null && CronPaths.IsDurableCronEnabled())
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
        }
    }

    private void ReloadJobs()
    {
        lock (_reloadLock)
        {
            // Debounce inside the lock to prevent TOCTOU: if the check were outside,
            // multiple FSW threads could all pass the check before any acquires the lock.
            if (DateTimeOffset.UtcNow - _lastReload < ReloadDebounce) return;

            if (!Directory.Exists(_cronDir)) return;

            var files = Directory.GetFiles(_cronDir, "*.json");
            List<CronJobEntry> loaded = [];

            foreach (var file in files)
            {
                try
                {
                    var json = File.ReadAllText(file);
                    var entry = JsonSerializer.Deserialize<CronJobEntry>(json);
                    if (entry == null) continue;

                    // Path traversal defence: reject entries whose Id contains characters
                    // that could escape the cron directory when used in file-path construction.
                    if (!CronPaths.IsValidJobId(entry.Id))
                    {
                        _logger.LogWarning(
                            "Skipping cron job file {File}: invalid ID '{Id}' (potential path traversal)", file, entry.Id);
                        continue;
                    }

                    // Durable enforcement: only load jobs whose Durable flag is true. A non-
                    // durable file shouldn't exist on disk in normal operation, but if one
                    // does (e.g. host downgraded ONECODE_DURABLE_CRON after creating durable
                    // jobs), we honour the file's Durable flag rather than the env switch —
                    // the env switch only governs whether *new* persists, not whether *old*
                    // resumes.
                    if (!entry.Durable)
                    {
                        _logger.LogDebug(
                            "Skipping non-durable cron file {File} on reload", file);
                        continue;
                    }

                    if (entry.NextRunAt == null && entry.Recurring && !entry.Paused)
                    {
                        var next = _cronParser.ComputeNextRun(entry.Cron, DateTimeOffset.UtcNow);
                        entry.NextRunAt = next?.ToUnixTimeSeconds();
                    }

                    if (loaded.Count + _jobs.Count(j => !j.Durable) + _pendingJobIds.Count >= MaxJobs)
                    {
                        _logger.LogWarning(
                            "Skipping cron job file {File}: scheduler limit {MaxJobs} reached", file, MaxJobs);
                        continue;
                    }

                    if (loaded.Any(j => string.Equals(j.Id, entry.Id, StringComparison.OrdinalIgnoreCase)) ||
                        _pendingJobIds.Contains(entry.Id))
                        continue;

                    loaded.Add(entry);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to parse cron job file {File}; skipping", file);
                }
            }

            // Merge instead of replace: in-memory (non-durable) jobs added via AddJob
            // are NOT backed by a JSON file, so ReloadJobs (triggered by FileSystemWatcher
            // when a *different* durable job is created) would otherwise drop them all.
            // We keep the durable set in sync with disk and preserve the in-memory set.
            var inMemoryOnly = _jobs.Where(j => !j.Durable).ToList();
            _jobs = inMemoryOnly.Concat(loaded).ToList();
            _lastReload = DateTimeOffset.UtcNow;
        }
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
