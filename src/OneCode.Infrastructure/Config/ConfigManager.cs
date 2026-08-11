using CoreConstants = OneCode.Core.Constants;

namespace OneCode.Infrastructure.Config;

/// <summary>
/// 统一配置服务。优先级固定为 Session &gt; Environment &gt; Project &gt; User &gt; BuiltIn。
/// 只有 User 和 Project 作用域会持久化；保存始终基于作用域原始值执行 Patch。
/// </summary>
public sealed class ConfigManager : IConfigManager, IDisposable
{
    private const int ReloadDebounceMs = 200;
    private readonly string _configDir;
    private readonly string? _projectConfigDir;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _applyGate = new(1, 1);
    private Dictionary<string, object?> _sessionValues = new(StringComparer.OrdinalIgnoreCase);
    private ConfigSnapshot _current = CreateDefaultSnapshot();
    private FileSystemWatcher? _globalWatcher;
    private FileSystemWatcher? _projectWatcher;
    private DateTime _lastReloadTime;

    public ConfigManager() : this(GetDefaultConfigDir(), projectConfigDir: null)
    {
    }

    public ConfigManager(string configDir) : this(configDir, projectConfigDir: null)
    {
    }

    public ConfigManager(string configDir, string? projectConfigDir)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configDir);
        _configDir = configDir;
        _projectConfigDir = string.IsNullOrWhiteSpace(projectConfigDir) ? null : projectConfigDir;
        Reload();
    }

    public ConfigSnapshot Current
    {
        get
        {
            lock (_gate)
                return _current;
        }
    }

    public string ConfigDir => _configDir;

    public string? ProjectConfigDir => _projectConfigDir;

    public string SettingsFilePath => Path.Combine(_configDir, Constants.App.SettingsFileName);

    public string? ProjectSettingsFilePath =>
        _projectConfigDir is null ? null : Path.Combine(_projectConfigDir, Constants.App.SettingsFileName);

    public event Action<ConfigSnapshot>? SettingsChanged;

    public void Reload()
    {
        ConfigSnapshot? changed = null;
        lock (_gate)
        {
            if (TryResolveSnapshot(_sessionValues, out var snapshot, out var error))
            {
                _current = snapshot;
                changed = snapshot;
            }
            else
            {
                Console.Error.WriteLine($"Warning: Failed to load settings: {error}");
            }
        }

        if (changed is not null)
            SettingsChanged?.Invoke(changed);
    }

    public async Task<ConfigApplyResult> ApplyAsync(ConfigPatch patch, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(patch);
        ValidatePatch(patch);

        await _applyGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await ApplyCoreAsync(patch, ct).ConfigureAwait(false);
        }
        finally
        {
            _applyGate.Release();
        }
    }

    private async Task<ConfigApplyResult> ApplyCoreAsync(ConfigPatch patch, CancellationToken ct)
    {
        ConfigSnapshot before;
        lock (_gate)
            before = _current;

        if (patch.Changes.Count == 0)
            return BuildResult(before, before, [], saved: true);

        if (patch.TargetScope == ConfigScope.Session)
        {
            ConfigSnapshot after;
            lock (_gate)
            {
                var nextSession = new Dictionary<string, object?>(_sessionValues, StringComparer.OrdinalIgnoreCase);
                ApplyMutations(nextSession, patch.Changes);
                if (!TryResolveSnapshot(nextSession, out after, out var error))
                    return ConfigApplyResult.Failure(before, error ?? "Failed to resolve session configuration.");

                _sessionValues = nextSession;
                _current = after;
            }

            SettingsChanged?.Invoke(after);
            return BuildResult(before, after, patch.Changes.Keys, saved: true);
        }

        var targetPath = GetWritablePath(patch.TargetScope);
        Dictionary<string, object?> targetValues;
        try
        {
            targetValues = await ReadScopeFileAsync(targetPath, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ConfigApplyResult.Failure(before, $"Failed to read {patch.TargetScope} configuration: {ex.Message}");
        }

        ApplyMutations(targetValues, patch.Changes);

        try
        {
            await WriteScopeFileAtomicAsync(targetPath, targetValues, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ConfigApplyResult.Failure(before, $"Failed to save {patch.TargetScope} configuration: {ex.Message}");
        }

        ConfigSnapshot resolved;
        lock (_gate)
        {
            if (!TryResolveSnapshot(_sessionValues, out resolved, out var error))
                return ConfigApplyResult.Failure(before, error ?? "Configuration was saved but could not be reloaded.");

            _current = resolved;
        }

        SettingsChanged?.Invoke(resolved);
        return BuildResult(before, resolved, patch.Changes.Keys, saved: true);
    }

    public void InitializeWatcher()
    {
        TryWatchFile(SettingsFilePath, ref _globalWatcher);
        if (ProjectSettingsFilePath is { } projectPath)
            TryWatchFile(projectPath, ref _projectWatcher);
    }

    private void TryWatchFile(string filePath, ref FileSystemWatcher? watcher)
    {
        var directory = Path.GetDirectoryName(filePath);
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
            return;

        try
        {
            watcher = new FileSystemWatcher(directory, Path.GetFileName(filePath))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                EnableRaisingEvents = true,
            };
            watcher.Changed += OnSettingsFileChanged;
            watcher.Created += OnSettingsFileChanged;
            watcher.Renamed += OnSettingsFileChanged;
        }
        catch
        {
            // 热重载是可选能力；初始化失败不影响配置读写。
        }
    }

    private void OnSettingsFileChanged(object sender, FileSystemEventArgs e)
    {
        lock (_gate)
        {
            if ((DateTime.UtcNow - _lastReloadTime).TotalMilliseconds < ReloadDebounceMs)
                return;
            _lastReloadTime = DateTime.UtcNow;
        }

        Reload();
    }

    private bool TryResolveSnapshot(
        IReadOnlyDictionary<string, object?> sessionValues,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out ConfigSnapshot? snapshot,
        out string? error)
    {
        try
        {
            var builtIn = CreateBuiltInValues();
            var user = ReadScopeFile(SettingsFilePath);
            var project = ProjectSettingsFilePath is { } projectPath
                ? ReadScopeFile(projectPath)
                : new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            var environment = ReadEnvironmentValues();
            var session = new Dictionary<string, object?>(sessionValues, StringComparer.OrdinalIgnoreCase);

            var layers = new (ConfigScope Scope, IReadOnlyDictionary<string, object?> Values)[]
            {
                (ConfigScope.BuiltIn, builtIn),
                (ConfigScope.User, user),
                (ConfigScope.Project, project),
                (ConfigScope.Environment, environment),
                (ConfigScope.Session, session),
            };

            var effective = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            var sources = new Dictionary<string, ConfigScope>(StringComparer.OrdinalIgnoreCase);
            foreach (var layer in layers)
            {
                foreach (var (key, value) in layer.Values)
                {
                    effective[key] = NormalizeValue(value);
                    sources[key] = layer.Scope;
                }
            }

            var infos = new Dictionary<string, ConfigValueInfo>(StringComparer.OrdinalIgnoreCase);
            foreach (var descriptor in SettingDescriptors.All)
            {
                effective.TryGetValue(descriptor.Key, out var value);
                sources.TryGetValue(descriptor.Key, out var source);
                var overridden = layers.Any(layer =>
                    GetRank(layer.Scope) < GetRank(source) && layer.Values.ContainsKey(descriptor.Key));
                infos[descriptor.Key] = new ConfigValueInfo(value, source, overridden);
            }

            var scoped = layers.ToDictionary(
                layer => layer.Scope,
                layer => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>(layer.Values, StringComparer.OrdinalIgnoreCase));

            snapshot = new ConfigSnapshot(new AppSettings(effective), infos, scoped);
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            snapshot = null;
            error = ex.Message;
            return false;
        }
    }

    private static ConfigApplyResult BuildResult(
        ConfigSnapshot before,
        ConfigSnapshot after,
        IEnumerable<string> patchedKeys,
        bool saved)
    {
        var patched = patchedKeys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        List<string> immediate = [];
        List<string> nextOperation = [];
        List<string> restart = [];
        List<string> overridden = [];

        foreach (var descriptor in SettingDescriptors.All)
        {
            var oldInfo = before.GetValueInfo(descriptor.Key);
            var newInfo = after.GetValueInfo(descriptor.Key);
            if (ValuesEqual(oldInfo.Value, newInfo.Value) && oldInfo.Source == newInfo.Source)
            {
                if (patched.Contains(descriptor.Key) && newInfo.IsOverridden)
                    overridden.Add(descriptor.Key);
                continue;
            }

            switch (descriptor.Activation)
            {
                case ActivationMode.Immediate:
                    immediate.Add(descriptor.Key);
                    break;
                case ActivationMode.NextOperation:
                    nextOperation.Add(descriptor.Key);
                    break;
                case ActivationMode.RestartRequired:
                    restart.Add(descriptor.Key);
                    break;
            }

            if (patched.Contains(descriptor.Key) && newInfo.IsOverridden)
                overridden.Add(descriptor.Key);
        }

        return new ConfigApplyResult(saved, after, immediate, nextOperation, restart, overridden);
    }

    private static bool ValuesEqual(object? left, object? right) =>
        JsonSerializer.Serialize(NormalizeValue(left)) == JsonSerializer.Serialize(NormalizeValue(right));

    private static void ValidatePatch(ConfigPatch patch)
    {
        if (patch.TargetScope is ConfigScope.BuiltIn or ConfigScope.Environment)
            throw new ArgumentException($"Configuration scope '{patch.TargetScope}' is read-only.", nameof(patch));

        foreach (var key in patch.Changes.Keys)
        {
            var descriptor = SettingDescriptors.Get(key);
            if (patch.TargetScope == ConfigScope.Project && !descriptor.AllowProjectScope)
                throw new ArgumentException($"Configuration key '{key}' cannot be written to project scope.", nameof(patch));
        }
    }

    private string GetWritablePath(ConfigScope scope) => scope switch
    {
        ConfigScope.User => SettingsFilePath,
        ConfigScope.Project when ProjectSettingsFilePath is { } path => path,
        ConfigScope.Project => throw new InvalidOperationException("Project configuration is not enabled."),
        _ => throw new ArgumentOutOfRangeException(nameof(scope), scope, "Scope is not persistent."),
    };

    private static void ApplyMutations(
        IDictionary<string, object?> values,
        IReadOnlyDictionary<string, ConfigMutation> changes)
    {
        foreach (var (key, mutation) in changes)
        {
            switch (mutation)
            {
                case ConfigMutation.Set set:
                    values[key] = SettingDescriptors.Coerce(SettingDescriptors.Get(key), NormalizeValue(set.Value));
                    break;
                case ConfigMutation.Remove:
                    values.Remove(key);
                    break;
            }
        }
    }

    private static Dictionary<string, object?> ReadScopeFile(string path)
    {
        if (!File.Exists(path))
            return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return FlattenSettings(document.RootElement, path);
    }

    private static async Task<Dictionary<string, object?>> ReadScopeFileAsync(string path, CancellationToken ct)
    {
        if (!File.Exists(path))
            return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        await using var stream = File.OpenRead(path);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        return FlattenSettings(document.RootElement, path);
    }

    private static Dictionary<string, object?> FlattenSettings(JsonElement root, string path)
    {
        if (root.ValueKind != JsonValueKind.Object)
            throw new JsonException($"Configuration file '{path}' must contain a JSON object.");

        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        FlattenObject(root, prefix: null, result);
        return result;
    }

    private static void FlattenObject(
        JsonElement element,
        string? prefix,
        IDictionary<string, object?> result)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (property.Name.Contains('.', StringComparison.Ordinal))
                throw new JsonException($"Configuration property '{property.Name}' must use nested JSON objects instead of dotted property names.");

            var key = prefix is null ? property.Name : $"{prefix}.{property.Name}";
            if (property.Value.ValueKind == JsonValueKind.Object)
            {
                FlattenObject(property.Value, key, result);
                continue;
            }

            if (!SettingDescriptors.TryGet(key, out var descriptor))
                throw new JsonException($"Unknown configuration key '{key}'.");

            result[descriptor.Key] = SettingDescriptors.Coerce(descriptor, ConvertJsonElement(property.Value));
        }
    }

    private static async Task WriteScopeFileAtomicAsync(
        string path,
        IReadOnlyDictionary<string, object?> values,
        CancellationToken ct)
    {
        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException($"Configuration path '{path}' has no parent directory.");
        Directory.CreateDirectory(directory);

        var tempPath = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            };
            var json = JsonSerializer.Serialize(ExpandSettings(values), options);
            await File.WriteAllTextAsync(tempPath, json, ct).ConfigureAwait(false);
            File.Move(tempPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }

    private static Dictionary<string, object?> ReadEnvironmentValues()
    {
        var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var descriptor in SettingDescriptors.All)
        {
            if (descriptor.EnvironmentVariable is null)
                continue;

            var raw = Environment.GetEnvironmentVariable(descriptor.EnvironmentVariable);
            if (!string.IsNullOrWhiteSpace(raw))
                values[descriptor.Key] = SettingDescriptors.Coerce(descriptor, raw);
        }

        return values;
    }

    private static Dictionary<string, object?> CreateBuiltInValues() =>
        SettingDescriptors.All
            .Where(descriptor => descriptor.BuiltInDefault is not null)
            .ToDictionary(
                descriptor => descriptor.Key,
                descriptor => NormalizeValue(descriptor.BuiltInDefault),
                StringComparer.OrdinalIgnoreCase);

    private static Dictionary<string, object?> ExpandSettings(
        IReadOnlyDictionary<string, object?> values)
    {
        var root = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in values.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase))
        {
            var segments = key.Split('.', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 0)
                continue;

            IDictionary<string, object?> current = root;
            for (var index = 0; index < segments.Length - 1; index++)
            {
                if (!current.TryGetValue(segments[index], out var child))
                {
                    child = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                    current[segments[index]] = child;
                }

                current = child as IDictionary<string, object?>
                    ?? throw new InvalidOperationException($"Configuration key '{key}' conflicts with another setting path.");
            }

            current[segments[^1]] = NormalizeValue(value);
        }

        return root;
    }

    private static ConfigSnapshot CreateDefaultSnapshot()
    {
        var builtIn = CreateBuiltInValues();
        var infos = SettingDescriptors.All.ToDictionary(
            descriptor => descriptor.Key,
            descriptor => new ConfigValueInfo(
                builtIn.TryGetValue(descriptor.Key, out var value) ? value : null,
                ConfigScope.BuiltIn,
                false),
            StringComparer.OrdinalIgnoreCase);
        var scoped = new Dictionary<ConfigScope, IReadOnlyDictionary<string, object?>>
        {
            [ConfigScope.BuiltIn] = builtIn,
            [ConfigScope.User] = new Dictionary<string, object?>(),
            [ConfigScope.Project] = new Dictionary<string, object?>(),
            [ConfigScope.Environment] = new Dictionary<string, object?>(),
            [ConfigScope.Session] = new Dictionary<string, object?>(),
        };
        return new ConfigSnapshot(new AppSettings(builtIn), infos, scoped);
    }

    private static object? ConvertJsonElement(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Null => null,
        JsonValueKind.String => element.GetString(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Number when element.TryGetInt64(out var integer) => integer,
        JsonValueKind.Number => element.GetDouble(),
        JsonValueKind.Array => element.EnumerateArray().Select(ConvertJsonElement).ToArray(),
        JsonValueKind.Object => element.Clone(),
        _ => element.GetRawText(),
    };

    private static object? NormalizeValue(object? value) => value switch
    {
        JsonElement element => ConvertJsonElement(element),
        IEnumerable<string> strings => strings.ToArray(),
        _ => value,
    };

    private static int GetRank(ConfigScope scope) => scope switch
    {
        ConfigScope.BuiltIn => 0,
        ConfigScope.User => 1,
        ConfigScope.Project => 2,
        ConfigScope.Environment => 3,
        ConfigScope.Session => 4,
        _ => -1,
    };

    private static string GetDefaultConfigDir() => PathsHelper.GetUserConfigDir();

    public void Dispose()
    {
        DisposeWatcher(ref _globalWatcher);
        DisposeWatcher(ref _projectWatcher);
        _applyGate.Dispose();
    }

    private void DisposeWatcher(ref FileSystemWatcher? watcher)
    {
        if (watcher is null)
            return;
        watcher.Changed -= OnSettingsFileChanged;
        watcher.Created -= OnSettingsFileChanged;
        watcher.Renamed -= OnSettingsFileChanged;
        watcher.Dispose();
        watcher = null;
    }
}

public sealed class AppSettings
{
    private readonly Dictionary<string, object?> _values;

    public AppSettings() => _values = new(StringComparer.OrdinalIgnoreCase);

    public AppSettings(Dictionary<string, object?> values) =>
        _values = new Dictionary<string, object?>(values ?? new(), StringComparer.OrdinalIgnoreCase);

    public string? ApiKey { get => Get<string>(CoreConstants.ConfigKeys.ApiKey); init => _values[CoreConstants.ConfigKeys.ApiKey] = value; }
    public string? BaseUrl { get => Get<string>(CoreConstants.ConfigKeys.BaseUrl); init => _values[CoreConstants.ConfigKeys.BaseUrl] = value; }
    public string? Provider { get => Get<string>(CoreConstants.ConfigKeys.Provider); init => _values[CoreConstants.ConfigKeys.Provider] = value; }
    public string? Model { get => Get<string>(CoreConstants.ConfigKeys.Model); init => _values[CoreConstants.ConfigKeys.Model] = value; }
    public int MaxTurns { get => Get(CoreConstants.ConfigKeys.MaxTurns, CoreConstants.Session.MaxTurnsDefault); init => _values[CoreConstants.ConfigKeys.MaxTurns] = value; }
    public double MaxBudgetUsd { get => Get(CoreConstants.ConfigKeys.MaxBudgetUsd, CoreConstants.Session.MaxBudgetUsdDefault); init => _values[CoreConstants.ConfigKeys.MaxBudgetUsd] = value; }
    public string PermissionMode { get => Get(CoreConstants.ConfigKeys.PermissionMode, CoreConstants.PermissionModes.Default) ?? CoreConstants.PermissionModes.Default; init => _values[CoreConstants.ConfigKeys.PermissionMode] = value; }
    public bool NextPromptSuggesterEnabled { get => Get(CoreConstants.ConfigKeys.NextPromptSuggesterEnabled, true); init => _values[CoreConstants.ConfigKeys.NextPromptSuggesterEnabled] = value; }
    public bool NotificationsEnabled { get => Get(CoreConstants.ConfigKeys.NotificationsEnabled, false); init => _values[CoreConstants.ConfigKeys.NotificationsEnabled] = value; }
    public int OllamaContextWindow { get => Get(CoreConstants.ConfigKeys.OllamaContextWindow, 32_768); init => _values[CoreConstants.ConfigKeys.OllamaContextWindow] = value; }
    public string WebSearchProvider { get => Get("webSearchProvider", "duckduckgo") ?? "duckduckgo"; init => _values["webSearchProvider"] = value; }
    public string? WebSearchApiKey { get => Get<string>("webSearchApiKey"); init => _values["webSearchApiKey"] = value; }
    public bool HasTrustAccepted { get => Get("hasTrustAccepted", false); init => _values["hasTrustAccepted"] = value; }
    public List<string> TrustedDirectories { get => GetStringList("trustedDirectories"); init => _values["trustedDirectories"] = value?.ToArray() ?? []; }
    public List<string> AllowedDirectories { get => GetStringList("allowedDirectories"); init => _values["allowedDirectories"] = value?.ToArray() ?? []; }

    public T? Get<T>(string key, T? defaultValue = default)
    {
        if (!_values.TryGetValue(key, out var value) || value is null)
            return defaultValue;
        if (value is T typed)
            return typed;

        try
        {
            var target = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);
            return (T?)Convert.ChangeType(value, target, CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is InvalidCastException or FormatException or OverflowException)
        {
            return defaultValue;
        }
    }

    public Dictionary<string, object?> ToDictionary() => new(_values, StringComparer.OrdinalIgnoreCase);

    private List<string> GetStringList(string key)
    {
        if (!_values.TryGetValue(key, out var value) || value is null)
            return [];
        return value switch
        {
            IEnumerable<string> strings => strings.ToList(),
            IEnumerable<object?> objects => objects.OfType<string>().ToList(),
            JsonElement element when element.ValueKind == JsonValueKind.Array =>
                element.EnumerateArray().Select(item => item.GetString()).OfType<string>().ToList(),
            _ => [],
        };
    }
}
