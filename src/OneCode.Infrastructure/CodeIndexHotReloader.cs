using OneCode.Infrastructure.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;

namespace OneCode.Infrastructure;

/// <summary>
/// Wraps <see cref="ICodeIndexService"/> with a <see cref="FileSystemWatcher"/>
/// that automatically re-indexes files when they are created, changed, or deleted.
///
/// Changes are debounced (default 500 ms) to coalesce rapid save-storms (e.g. IDE
/// auto-format on save) into a single <see cref="ICodeIndexService.UpdateFilesAsync"/> call.
///
/// Dispose to stop watching.
/// </summary>
public sealed class CodeIndexHotReloader : IDisposable
{
    private readonly ICodeIndexService _indexService;
    private readonly ILogger<CodeIndexHotReloader> _logger;

    private readonly object _lock = new();
    private readonly HashSet<string> _pendingChanged = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _pendingRemoved = new(StringComparer.OrdinalIgnoreCase);
    private Timer? _debounceTimer;
    private FileSystemWatcher? _watcher;
    private bool _disposed;

    /// <summary>Debounce window in milliseconds (default 500).</summary>
    public int DebounceMs { get; init; } = 500;

    public CodeIndexHotReloader(
        ICodeIndexService indexService,
        ILogger<CodeIndexHotReloader>? logger = null)
    {
        _indexService = indexService ?? throw new ArgumentNullException(nameof(indexService));
        _logger = logger ?? NullLogger<CodeIndexHotReloader>.Instance;
    }

    /// <summary>
    /// Start watching <paramref name="rootDirectory"/> for source-file changes.
    /// Safe to call multiple times — stops any previous watcher first.
    /// </summary>
    public void StartWatching(string rootDirectory)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        StopWatching();

        if (!Directory.Exists(rootDirectory))
        {
            _logger.LogWarning("Hot-reload: directory does not exist: {Dir}", rootDirectory);
            return;
        }

        var watcher = new FileSystemWatcher(rootDirectory)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.DirectoryName,
            // Watch all files — filter to source extensions in the event handler
            Filter = "*.*",
            EnableRaisingEvents = true,
        };

        watcher.Changed += OnFileEvent;
        watcher.Created += OnFileEvent;
        watcher.Deleted += OnDeletedEvent;
        watcher.Renamed += OnRenamedEvent;
        watcher.Error += OnWatcherError;

        _watcher = watcher;
        _logger.LogInformation("Hot-reload watching: {Dir}", rootDirectory);
    }

    /// <summary>Stop watching without disposing the reloader.</summary>
    public void StopWatching()
    {
        FileSystemWatcher? old;
        lock (_lock) { old = _watcher; _watcher = null; }
        if (old is null) return;
        old.EnableRaisingEvents = false;
        old.Changed -= OnFileEvent;
        old.Created -= OnFileEvent;
        old.Deleted -= OnDeletedEvent;
        old.Renamed -= OnRenamedEvent;
        old.Error -= OnWatcherError;
        old.Dispose();
    }

    // Event handlers

    private void OnFileEvent(object _, FileSystemEventArgs e)
    {
        if (!IsSourceFile(e.FullPath)) return;
        lock (_lock)
        {
            _pendingChanged.Add(e.FullPath);
            ScheduleFlush();
        }
    }

    private void OnDeletedEvent(object _, FileSystemEventArgs e)
    {
        if (!IsSourceFile(e.FullPath)) return;
        lock (_lock)
        {
            _pendingRemoved.Add(e.FullPath);
            _pendingChanged.Remove(e.FullPath); // no point re-indexing a deleted file
            ScheduleFlush();
        }
    }

    private void OnRenamedEvent(object _, RenamedEventArgs e)
    {
        lock (_lock)
        {
            // Old path is gone
            if (IsSourceFile(e.OldFullPath))
            {
                _pendingRemoved.Add(e.OldFullPath);
                _pendingChanged.Remove(e.OldFullPath);
            }
            // New path may be a source file
            if (IsSourceFile(e.FullPath))
            {
                _pendingChanged.Add(e.FullPath);
            }
            ScheduleFlush();
        }
    }

    private void OnWatcherError(object _, ErrorEventArgs e) =>
        _logger.LogWarning(e.GetException(), "FileSystemWatcher error");

    // Debounce

    /// <summary>Reset (or start) the debounce timer. Must be called inside <see cref="_lock"/>.</summary>
    private void ScheduleFlush()
    {
        if (_debounceTimer is null)
            _debounceTimer = new Timer(FlushCallback, null, DebounceMs, Timeout.Infinite);
        else
            _debounceTimer.Change(DebounceMs, Timeout.Infinite); // reset window
    }

    private void FlushCallback(object? _)
    {
        List<string> changed;
        List<string> removed;

        lock (_lock)
        {
            changed = new List<string>(_pendingChanged);
            removed = new List<string>(_pendingRemoved);
            _pendingChanged.Clear();
            _pendingRemoved.Clear();
            _debounceTimer?.Dispose();
            _debounceTimer = null;
        }

        if (changed.Count == 0 && removed.Count == 0) return;

        _logger.LogInformation(
            "Hot-reload: updating {Changed} changed, {Removed} removed files",
            changed.Count, removed.Count);

        // Fire-and-forget; errors are logged but not propagated to the watcher thread
        _ = _indexService.UpdateFilesAsync(changed, removed).ContinueWith(t =>
        {
            if (t.IsFaulted)
                _logger.LogError(t.Exception, "Hot-reload UpdateFilesAsync failed");
        }, TaskScheduler.Default);
    }

    // Helpers

    private static readonly HashSet<string> _sourceExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".ts", ".tsx", ".js", ".jsx",
        ".py", ".go", ".java", ".rs",
        ".vb", ".fs", ".fsx",
    };

    private static bool IsSourceFile(string path) =>
        _sourceExtensions.Contains(Path.GetExtension(path));

    // IDisposable

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopWatching();
        lock (_lock)
        {
            _debounceTimer?.Dispose();
            _debounceTimer = null;
        }
    }
}
