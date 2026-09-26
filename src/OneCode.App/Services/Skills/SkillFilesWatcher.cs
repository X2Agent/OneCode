using System.Threading.Channels;
using Microsoft.Extensions.Hosting;

namespace OneCode.App.Services.Skills;

/// <summary>
/// Notifies interested UI surfaces when skill definition files change on disk.
/// </summary>
/// <remarks>
/// <para>
/// <b>This type does not build or swap skills providers.</b> Agent-visible skills are resolved by
/// <c>SkillProviderFactory</c> on every agent run, and slash-command discovery reads the filesystem
/// directly through <see cref="SkillCatalog"/>. Neither needs a rebuild, so this service only raises
/// <see cref="SkillsChanged"/> so the TUI can re-render.
/// </para>
/// <para>
/// File-system events are bursty (an editor save can raise several), so changes are debounced
/// through a bounded channel before the event fires.
/// </para>
/// </remarks>
public sealed class SkillFilesWatcher(
    ILogger<SkillFilesWatcher> logger,
    SkillCatalog catalog) : BackgroundService
{
    private static readonly TimeSpan DebounceWindow = TimeSpan.FromMilliseconds(300);

    private readonly ILogger<SkillFilesWatcher> _logger = logger;
    private readonly SkillCatalog _catalog = catalog;

    private readonly Channel<string> _changeChannel =
        Channel.CreateBounded<string>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });

    private readonly List<FileSystemWatcher> _watchers = [];

    /// <summary>
    /// Raised after a debounced batch of skill file changes. Subscribers should re-run
    /// <see cref="Commands.SkillCommandSource.LoadCommandsAsync"/> to pick up new or removed
    /// skill slash commands.
    /// </summary>
    public event Action? SkillsChanged;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        SetupWatchers();
        await ProcessChangesAsync(stoppingToken).ConfigureAwait(false);
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        foreach (var watcher in _watchers)
            watcher.Dispose();
        _watchers.Clear();
        return base.StopAsync(cancellationToken);
    }

    private void SetupWatchers()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var dir in _catalog.GetSkillDirectories())
        {
            if (!Directory.Exists(dir)) continue;
            var realDir = Path.GetFullPath(dir);
            if (!seen.Add(realDir)) continue;
            try
            {
                var watcher = new FileSystemWatcher(dir)
                {
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.DirectoryName,
                    Filter = "*",
                    IncludeSubdirectories = true,
                    EnableRaisingEvents = true,
                };

                void Enqueue(object _, FileSystemEventArgs e) => EnqueueChange(e.FullPath);
                void EnqueueRename(object _, RenamedEventArgs e) => EnqueueChange(e.FullPath);

                watcher.Changed += Enqueue;
                watcher.Created += Enqueue;
                watcher.Deleted += Enqueue;
                watcher.Renamed += EnqueueRename;

                _watchers.Add(watcher);
                _logger.LogDebug("SkillFilesWatcher watching: {Dir}", dir);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Cannot watch skill dir: {Dir}", dir);
            }
        }
    }

    private void EnqueueChange(string path)
    {
        var ext = Path.GetExtension(path);
        if (!string.IsNullOrEmpty(ext) && ext is not (".md" or ".yaml" or ".yml")) return;
        _changeChannel.Writer.TryWrite(path);
    }

    private async Task ProcessChangesAsync(CancellationToken ct)
    {
        var reader = _changeChannel.Reader;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await reader.ReadAsync(ct).ConfigureAwait(false);

                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(DebounceWindow);
                try
                {
                    while (await reader.WaitToReadAsync(cts.Token).ConfigureAwait(false))
                        reader.TryRead(out _);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }

                _logger.LogDebug("Skill files changed; notifying subscribers");
                SkillsChanged?.Invoke();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (ChannelClosedException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in SkillFilesWatcher loop");
            }
        }
    }
}
