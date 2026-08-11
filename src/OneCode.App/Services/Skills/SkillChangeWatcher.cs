// MAAI001 suppressed: dynamic AgentSkillsProvider rebuild triggers experimental API warning
using System.Threading.Channels;
using Microsoft.Agents.AI;
using Microsoft.Extensions.Hosting;
using OneCode.Infrastructure.Config;
namespace OneCode.App.Services.Skills;

/// <summary>
/// Watches skill directories for file changes and rebuilds the AgentSkillsProvider
/// with a 300 ms Channel-based debounce (Phase 2-B).
/// Fixes: IncludeSubdirectories=true, Channel debounce, factory-based rebuild.
/// </summary>
public sealed class SkillChangeWatcher : BackgroundService
{
    private readonly ILogger<SkillChangeWatcher> _logger;
    private readonly SkillProviderHolder _holder;
    private readonly Func<Task<AgentSkillsProvider>> _providerFactory;
    private readonly SkillCatalog _catalog;

    private readonly Channel<string> _changeChannel =
        Channel.CreateBounded<string>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });

    private readonly List<FileSystemWatcher> _watchers = [];

    /// <summary>
    /// Fires after the <see cref="AgentSkillsProvider"/> is successfully rebuilt following
    /// a file-system change. Subscribers should re-run <see cref="Commands.SkillCommandSource.LoadCommandsAsync"/>
    /// to pick up new or removed skill slash commands.
    /// </summary>
    public event Action? SkillsChanged;

    public SkillChangeWatcher(
        ILogger<SkillChangeWatcher> logger,
        SkillProviderHolder holder,
        Func<Task<AgentSkillsProvider>> providerFactory,
        SkillCatalog catalog)
    {
        _logger = logger;
        _holder = holder;
        _providerFactory = providerFactory;
        _catalog = catalog;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        SetupWatchers();
        await ProcessChangesAsync(stoppingToken).ConfigureAwait(false);
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
                var w = new FileSystemWatcher(dir)
                {
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.DirectoryName,
                    Filter = "*",
                    IncludeSubdirectories = true,
                    EnableRaisingEvents = true,
                };

                void Enqueue(object _, FileSystemEventArgs e) => EnqueueChange(e.FullPath);
                void EnqueueRename(object _, RenamedEventArgs e) => EnqueueChange(e.FullPath);

                w.Changed += Enqueue;
                w.Created += Enqueue;
                w.Deleted += Enqueue;
                w.Renamed += EnqueueRename;

                _watchers.Add(w);
                _logger.LogDebug("SkillChangeWatcher watching: {Dir}", dir);
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

    /// <summary>
    /// Dynamically discovers and watches <c>.onecode/skills</c> directories
    /// under the given base paths. Call this after file-tool operations that may
    /// have created new skill directories (e.g., project clone, init, unzip).
    /// New watchers are added without disrupting existing ones.
    /// </summary>
    public void DiscoverAndWatchSkillDirs(IEnumerable<string> basePaths)
    {
        bool anyNew = false;
        foreach (var basePath in basePaths)
        {
            // Scan all candidate dir names (.onecode/.agent/.claude) under each base path.
            foreach (var candidateDir in ConfigDirPaths.EnumerateExisting(basePath, Constants.Subdirs.Skills))
            {
                var realDir = Path.GetFullPath(candidateDir);
                if (_watchers.Any(w => string.Equals(
                        Path.GetFullPath(w.Path), realDir, StringComparison.OrdinalIgnoreCase)))
                    continue;  // already watched

                try
                {
                    var w = new FileSystemWatcher(realDir)
                    {
                        NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.DirectoryName,
                        Filter = "*",
                        IncludeSubdirectories = true,
                        EnableRaisingEvents = true,
                    };
                    void Enqueue(object _, FileSystemEventArgs e) => EnqueueChange(e.FullPath);
                    void EnqueueRename(object _, RenamedEventArgs e) => EnqueueChange(e.FullPath);
                    w.Changed += Enqueue;
                    w.Created += Enqueue;
                    w.Deleted += Enqueue;
                    w.Renamed += EnqueueRename;
                    _watchers.Add(w);
                    anyNew = true;
                    _logger.LogInformation("DiscoverAndWatch — now watching {Dir}", realDir);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Cannot watch newly discovered skill dir: {Dir}", realDir);
                }
            }
        }

        // Trigger a provider rebuild if new directories were found
        if (anyNew)
            _changeChannel.Writer.TryWrite("[dynamic-discovery]");
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
                cts.CancelAfter(TimeSpan.FromMilliseconds(300));
                try
                {
                    while (await reader.WaitToReadAsync(cts.Token).ConfigureAwait(false))
                        reader.TryRead(out _);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }

                await RebuildProviderAsync().ConfigureAwait(false);
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
                _logger.LogError(ex, "Error in SkillChangeWatcher loop");
            }
        }
    }

    private async Task RebuildProviderAsync()
    {
        try
        {
            var newProvider = await _providerFactory().ConfigureAwait(false);
            _holder.Replace(newProvider);
            _logger.LogInformation("AgentSkillsProvider rebuilt after skill file change (including MCP skills)");
            SkillsChanged?.Invoke();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to rebuild AgentSkillsProvider");
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _changeChannel.Writer.TryComplete();
        foreach (var w in _watchers) { w.EnableRaisingEvents = false; w.Dispose(); }
        _watchers.Clear();
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }
}

