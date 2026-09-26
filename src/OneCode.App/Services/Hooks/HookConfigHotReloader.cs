namespace OneCode.App.Services.Hooks;

/// <summary>
/// Hook 配置热重载器——监视 hooks.json / notification-providers.json 变更，
/// 防抖后整体重建 HookRegistry（不做增量 Bootstrap）。
///
/// 设计要点（对齐 CodeIndexHotReloader / SkillFilesWatcher 既有模式）：
/// - FileSystemWatcher 仅监视两层配置目录顶层（不含子目录），文件名过滤 hooks.json 与 notification-providers.json
/// - Timer 防抖合并保存风暴（编辑器"保存时格式化"等）为单次重建
/// - 整体重建走 <see cref="HookConfigBootstrapper.Build"/> 快照，经 <see cref="HookRegistry.ReplaceAll"/> 原子交换；
///   编程注册（名称不带 config: 前缀）的 hook 全程保留
/// - 任一层配置解析失败（Failed）或构建异常时保留上一次有效配置（last-good），不因瞬时坏文件清空 hook
/// - Dispose 停止全部 watcher（DI 容器在宿主停止时自动释放单例）
/// </summary>
public sealed class HookConfigHotReloader(
    HookConfigBootstrapper bootstrapper,
    HookRegistry registry,
    HookLoadDiagnostics loadDiagnostics,
    NotificationProviderRegistry providerRegistry,
    ILogger<HookConfigHotReloader> logger) : IDisposable
{
    private static readonly HashSet<string> WatchedFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "hooks.json",
        "notification-providers.json",
    };

    private readonly HookConfigBootstrapper _bootstrapper = bootstrapper ?? throw new ArgumentNullException(nameof(bootstrapper));
    private readonly HookRegistry _registry = registry ?? throw new ArgumentNullException(nameof(registry));
    private readonly HookLoadDiagnostics _loadDiagnostics = loadDiagnostics ?? throw new ArgumentNullException(nameof(loadDiagnostics));
    private readonly NotificationProviderRegistry _providerRegistry = providerRegistry ?? throw new ArgumentNullException(nameof(providerRegistry));
    private readonly ILogger<HookConfigHotReloader> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    private readonly object _lock = new();
    private readonly List<FileSystemWatcher> _watchers = [];
    private Timer? _debounceTimer;
    private bool _disposed;

    private string _userConfigDir = string.Empty;
    private string? _projectConfigDir;

    /// <summary>防抖窗口毫秒数（默认 500，与 CodeIndexHotReloader 一致）。</summary>
    public int DebounceMs { get; init; } = 500;

    /// <summary>
    /// 启动路径：先执行一次完整 Bootstrap，再开始监视配置目录。
    /// 供 AppStartupService 单点调用，保证 watcher 启动时注册表已是初始状态。
    /// </summary>
    /// <param name="userConfigDir">用户配置目录（~/.onecode）。</param>
    /// <param name="projectConfigDir">项目配置目录（.onecode，可选）。</param>
    public void BootstrapAndStartWatching(string userConfigDir, string? projectConfigDir)
    {
        _bootstrapper.Bootstrap(userConfigDir, projectConfigDir);
        StartWatching(userConfigDir, projectConfigDir);
    }

    /// <summary>
    /// 开始监视用户/项目配置目录（目录不存在则跳过该层并记 Debug 日志）。
    /// 可重复调用——先停掉旧 watcher。
    /// </summary>
    public void StartWatching(string userConfigDir, string? projectConfigDir)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        StopWatching();
        _userConfigDir = userConfigDir ?? string.Empty;
        _projectConfigDir = projectConfigDir;

        WatchDirectory(_userConfigDir);
        if (!string.IsNullOrEmpty(_projectConfigDir))
            WatchDirectory(_projectConfigDir);
    }

    /// <summary>停止监视但不释放重载器（供 StartWatching 重入与宿主停止前手动调用）。</summary>
    public void StopWatching()
    {
        lock (_lock)
        {
            foreach (var watcher in _watchers)
            {
                watcher.EnableRaisingEvents = false;
                watcher.Dispose();
            }
            _watchers.Clear();
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
                return;
            _disposed = true;
            _debounceTimer?.Dispose();
            _debounceTimer = null;
        }

        StopWatching();
    }

    private void WatchDirectory(string configDir)
    {
        if (!Directory.Exists(configDir))
        {
            _logger.LogDebug("Hook 热重载：目录不存在，跳过监视 {Dir}", configDir);
            return;
        }

        try
        {
            var watcher = new FileSystemWatcher(configDir)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
                // 文件名过滤（hooks.json / notification-providers.json）在事件处理器中做
                Filter = "*.*",
                IncludeSubdirectories = false,
                EnableRaisingEvents = true,
            };

            void OnConfigEvent(object _, FileSystemEventArgs e) => ScheduleRebuild(e.Name);
            void OnRenamed(object _, RenamedEventArgs e) => ScheduleRebuild(e.Name);

            watcher.Changed += OnConfigEvent;
            watcher.Created += OnConfigEvent;
            watcher.Deleted += OnConfigEvent;
            watcher.Renamed += OnRenamed;
            watcher.Error += (_, ex) =>
                _logger.LogWarning(ex.GetException(), "Hook 热重载 FileSystemWatcher 错误");

            _watchers.Add(watcher);
            _logger.LogInformation("Hook 热重载监视中: {Dir}", configDir);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Hook 热重载：无法监视配置目录 {Dir}", configDir);
        }
    }

    private void ScheduleRebuild(string? fileName)
    {
        if (fileName is null || !WatchedFileNames.Contains(Path.GetFileName(fileName)))
            return;

        lock (_lock)
        {
            if (_disposed)
                return;

            if (_debounceTimer is null)
                _debounceTimer = new Timer(RebuildCallback, null, DebounceMs, System.Threading.Timeout.Infinite);
            else
                _debounceTimer.Change(DebounceMs, System.Threading.Timeout.Infinite); // 重置防抖窗口
        }
    }

    private void RebuildCallback(object? _)
    {
        lock (_lock)
        {
            _debounceTimer?.Dispose();
            _debounceTimer = null;
        }

        Rebuild();
    }

    /// <summary>
    /// 整体重建：Build 快照 → 校验 → HookRegistry.ReplaceAll 原子交换 + 刷新诊断与通知渠道定义。
    /// 任何失败路径（任一层解析失败 / 构建异常）都保留上一次有效配置。
    /// </summary>
    private void Rebuild()
    {
        try
        {
            var snapshot = _bootstrapper.Build(_userConfigDir, _projectConfigDir);
            _loadDiagnostics.Record(snapshot.Reports);

            if (snapshot.Reports.Any(r => r.Status == HookFileLoadStatus.Failed))
            {
                var errors = string.Join("; ",
                    snapshot.Reports.Where(r => r.Status == HookFileLoadStatus.Failed).SelectMany(r => r.Errors));
                _logger.LogWarning("Hook 热重载：配置解析失败，保留上一次有效配置（{Errors}）", errors);
                return;
            }

            // 整体交换：编程注册的 hook（名称无 config: 前缀）原样保留
            var preserved = _registry.GetAll()
                .Where(h => !h.Name.StartsWith(HookConfigBootstrapper.ConfigNamePrefix, StringComparison.Ordinal));

            _registry.ReplaceAll(preserved.Concat(snapshot.Registrations));
            _providerRegistry.SetDefinitions(snapshot.ProviderDefinitions);

            _logger.LogInformation("Hook 热重载完成：{Count} hooks", snapshot.Registrations.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Hook 热重载失败，保留上一次有效配置");
        }
    }
}