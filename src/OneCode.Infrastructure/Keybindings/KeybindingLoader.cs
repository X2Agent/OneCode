using OneCode.Core.Product;

using OneCode.Core.Keybindings;

namespace OneCode.Infrastructure.Keybindings;

/// <summary>
/// keybindings.json 文件的顶层 JSON 结构。
/// </summary>
internal sealed class KeybindingsConfig
{
    [JsonPropertyName("$schema")]
    public string? Schema { get; set; }

    [JsonPropertyName("bindings")]
    public List<KeybindingBlockJson>? Bindings { get; set; }
}

/// <summary>
/// JSON 中的绑定块结构。
/// </summary>
internal sealed class KeybindingBlockJson
{
    [JsonPropertyName("context")]
    public string Context { get; set; } = string.Empty;

    [JsonPropertyName("bindings")]
    public Dictionary<string, string?> Bindings { get; set; } = [];
}

/// <summary>
/// 用户配置加载器，从 ~/.onecode/keybindings.json 加载并合并默认绑定。
/// 支持热重载（FileSystemWatcher）。
/// </summary>
public sealed class KeybindingLoader : IDisposable
{
    private const int FileStabilityThresholdMs = 500;

    private readonly ILogger _logger;
    private readonly string _keybindingsPath;
    private FileSystemWatcher? _watcher;
    private KeybindingEntry[]? _cachedBindings;
    private KeybindingWarning[] _cachedWarnings = [];
    private DateTime _lastReloadTime;
    private readonly object _lock = new();

    /// <summary>
    /// 绑定变更事件，当文件变化重新加载后触发。
    /// </summary>
    public event Action<KeybindingsLoadResult>? BindingsChanged;

    public KeybindingLoader(ILogger<KeybindingLoader> logger)
    {
        _logger = logger;
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        _keybindingsPath = Path.Combine(home, ProductInfo.Default.ConfigDirName, "keybindings.json");
    }

    /// <summary>
    /// 获取 keybindings.json 文件路径。
    /// </summary>
    public string KeybindingsPath => _keybindingsPath;

    /// <summary>
    /// 获取缓存的绑定条目。
    /// </summary>
    public KeybindingEntry[]? CachedBindings => _cachedBindings;

    /// <summary>
    /// 获取缓存的验证警告。
    /// </summary>
    public KeybindingWarning[] CachedWarnings => _cachedWarnings;

    /// <summary>
    /// 异步加载键绑定配置，合并默认和用户绑定。
    /// </summary>
    public async Task<KeybindingsLoadResult> LoadAsync(CancellationToken ct = default)
    {
        var defaultBindings = KeybindingDefaults.GetDefaultParsedBindings();

        if (!File.Exists(_keybindingsPath))
        {
            return new KeybindingsLoadResult([.. defaultBindings], []);
        }

        try
        {
            var content = await File.ReadAllTextAsync(_keybindingsPath, ct).ConfigureAwait(false);
            var result = ParseAndMerge(content, defaultBindings);

            lock (_lock)
            {
                _cachedBindings = result.Bindings;
                _cachedWarnings = result.Warnings;
            }

            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Error loading keybindings from {Path}", _keybindingsPath);
            return new KeybindingsLoadResult(
                [.. defaultBindings],
                [new KeybindingWarning(
                    KeybindingWarningType.ParseError,
                    KeybindingSeverity.Error,
                    $"Failed to parse keybindings.json: {ex.Message}")]);
        }
    }

    /// <summary>
    /// 同步加载键绑定配置（用于初始化渲染）。
    /// 使用缓存值（如果可用）。
    /// </summary>
    public KeybindingsLoadResult LoadSync()
    {
        lock (_lock)
        {
            if (_cachedBindings is not null)
            {
                return new KeybindingsLoadResult(_cachedBindings, _cachedWarnings);
            }
        }

        var defaultBindings = KeybindingDefaults.GetDefaultParsedBindings();

        if (!File.Exists(_keybindingsPath))
        {
            var emptyResult = new KeybindingsLoadResult([.. defaultBindings], []);
            lock (_lock)
            {
                _cachedBindings = emptyResult.Bindings;
                _cachedWarnings = emptyResult.Warnings;
            }
            return emptyResult;
        }

        try
        {
            var content = File.ReadAllText(_keybindingsPath);
            var result = ParseAndMerge(content, defaultBindings);

            lock (_lock)
            {
                _cachedBindings = result.Bindings;
                _cachedWarnings = result.Warnings;
            }

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error loading keybindings from {Path}", _keybindingsPath);
            var errorResult = new KeybindingsLoadResult(
                [.. defaultBindings],
                [new KeybindingWarning(
                    KeybindingWarningType.ParseError,
                    KeybindingSeverity.Error,
                    $"Failed to parse keybindings.json: {ex.Message}")]);

            lock (_lock)
            {
                _cachedBindings = errorResult.Bindings;
                _cachedWarnings = errorResult.Warnings;
            }

            return errorResult;
        }
    }

    /// <summary>
    /// 初始化文件监视器以支持热重载。
    /// </summary>
    public void InitializeWatcher()
    {
        var watchDir = Path.GetDirectoryName(_keybindingsPath);
        if (string.IsNullOrEmpty(watchDir) || !Directory.Exists(watchDir))
        {
            _logger.LogDebug("Not watching: {Dir} does not exist", watchDir);
            return;
        }

        try
        {
            _watcher = new FileSystemWatcher(watchDir, Path.GetFileName(_keybindingsPath))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                EnableRaisingEvents = true,
            };

            _watcher.Changed += OnFileChanged;
            _watcher.Created += OnFileChanged;
            _watcher.Deleted += OnFileDeleted;
            _watcher.Renamed += OnFileDeleted;

            _logger.LogDebug("Watching for changes to {Path}", _keybindingsPath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to initialize file watcher for {Path}", _keybindingsPath);
        }
    }

    /// <summary>
    /// 重置内部状态（用于测试）。
    /// </summary>
    public void Reset()
    {
        lock (_lock)
        {
            _cachedBindings = null;
            _cachedWarnings = [];
        }
    }

    public void Dispose()
    {
        if (_watcher is not null)
        {
            _watcher.Changed -= OnFileChanged;
            _watcher.Created -= OnFileChanged;
            _watcher.Deleted -= OnFileDeleted;
            _watcher.Renamed -= OnFileDeleted;
            _watcher.Dispose();
            _watcher = null;
        }
    }

    private void OnFileChanged(object sender, FileSystemEventArgs e)
    {
        // 稳定阈值：等待文件写入完成
        var elapsed = (DateTime.UtcNow - _lastReloadTime).TotalMilliseconds;
        if (elapsed < FileStabilityThresholdMs)
        {
            return;
        }

        _lastReloadTime = DateTime.UtcNow;

        _logger.LogDebug("Detected change to {Path}", e.FullPath);

        try
        {
            var result = LoadSync();
            BindingsChanged?.Invoke(result);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error reloading keybindings");
        }
    }

    private void OnFileDeleted(object sender, FileSystemEventArgs e)
    {
        _logger.LogDebug("Detected deletion of {Path}", e.FullPath);

        var defaultBindings = KeybindingDefaults.GetDefaultParsedBindings();
        var result = new KeybindingsLoadResult([.. defaultBindings], []);

        lock (_lock)
        {
            _cachedBindings = result.Bindings;
            _cachedWarnings = [];
        }

        BindingsChanged?.Invoke(result);
    }

    private KeybindingsLoadResult ParseAndMerge(string content, List<KeybindingEntry> defaultBindings)
    {
        var config = JsonSerializer.Deserialize<KeybindingsConfig>(content);

        if (config?.Bindings is null || config.Bindings.Count == 0)
        {
            return new KeybindingsLoadResult(
                [.. defaultBindings],
                [new KeybindingWarning(
                    KeybindingWarningType.ParseError,
                    KeybindingSeverity.Error,
                    "keybindings.json must have a \"bindings\" array",
                    Suggestion: "Use format: { \"bindings\": [ ... ] }")]);
        }

        var userBlocks = new List<KeybindingBlock>();
        foreach (var block in config.Bindings)
        {
            if (string.IsNullOrEmpty(block.Context))
            {
                continue;
            }
            userBlocks.Add(new KeybindingBlock(block.Context, block.Bindings));
        }

        var userParsed = KeybindingParser.ParseBindings(userBlocks);

        _logger.LogDebug("Loaded {Count} user bindings from {Path}", userParsed.Count, _keybindingsPath);

        // 用户绑定追加在默认之后，后匹配的生效
        var mergedBindings = new List<KeybindingEntry>(defaultBindings);
        mergedBindings.AddRange(userParsed);

        var duplicateKeyWarnings = KeybindingValidator.CheckDuplicateKeysInJson(content);
        var validationWarnings = KeybindingValidator.ValidateBindings(userBlocks);

        var allWarnings = new List<KeybindingWarning>();
        allWarnings.AddRange(duplicateKeyWarnings);
        allWarnings.AddRange(validationWarnings);

        if (allWarnings.Count > 0)
        {
            _logger.LogDebug("Found {Count} validation issue(s)", allWarnings.Count);
        }

        return new KeybindingsLoadResult([.. mergedBindings], [.. allWarnings]);
    }
}
