using OneCode.Core.Permissions.Yolo;
using OneCode.Core.Product;

namespace OneCode.Infrastructure.Permissions.Yolo;

/// <summary>
/// Loads / saves <c>~/.onecode/yolo_rules.json</c>. Core <see cref="YoloRuleStore"/> stays in-memory only.
/// </summary>
public sealed class YoloRuleFileStore : IYoloRuleFileStore
{
    private readonly string _rulesPath;
    private readonly ILogger<YoloRuleFileStore>? _logger;

    public YoloRuleFileStore(ILogger<YoloRuleFileStore>? logger = null)
        : this(GetDefaultRulesPath(), logger)
    {
    }

    public YoloRuleFileStore(string rulesPath, ILogger<YoloRuleFileStore>? logger = null)
    {
        _rulesPath = rulesPath;
        _logger = logger;
    }

    public string RulesPath => _rulesPath;

    /// <summary>
    /// Load user rules from disk. Returns null when the file is missing or unreadable.
    /// Empty file yields an empty list (caller should fall back to built-ins).
    /// </summary>
    public async Task<IReadOnlyList<UserRule>?> TryLoadAsync(CancellationToken ct = default)
    {
        if (!File.Exists(_rulesPath))
            return null;

        try
        {
            var json = await File.ReadAllTextAsync(_rulesPath, ct).ConfigureAwait(false);
            var loaded = JsonSerializer.Deserialize<List<UserRule>>(json, JsonHelper.Options);
            _logger?.LogInformation("Loaded {Count} YOLO rules from {Path}", loaded?.Count ?? 0, _rulesPath);
            return loaded;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to load YOLO rules from {Path}", _rulesPath);
            return null;
        }
    }

    /// <summary>
    /// Load user rules, or built-in defaults when missing/empty/unreadable.
    /// </summary>
    public async Task<IReadOnlyList<UserRule>> LoadOrDefaultsAsync(CancellationToken ct = default)
    {
        var loaded = await TryLoadAsync(ct).ConfigureAwait(false);
        if (loaded is null || loaded.Count == 0)
        {
            var defaults = YoloRuleStore.GetBuiltInDefaultRules();
            _logger?.LogInformation(
                "No user YOLO rules found at {Path}; using {Count} built-in default rules",
                _rulesPath, defaults.Count);
            return defaults;
        }

        return loaded;
    }

    public async Task SaveAsync(IReadOnlyList<UserRule> rules, CancellationToken ct = default)
    {
        try
        {
            var dir = Path.GetDirectoryName(_rulesPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(rules, JsonHelper.Options);
            await File.WriteAllTextAsync(_rulesPath, json, ct).ConfigureAwait(false);
            _logger?.LogDebug("Saved {Count} YOLO rules to {Path}", rules.Count, _rulesPath);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to save YOLO rules to {Path}", _rulesPath);
        }
    }

    public static string GetDefaultRulesPath()
    {
        var homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(homeDir, ProductInfo.Default.ConfigDirName, "yolo_rules.json");
    }

    private static class JsonHelper
    {
        public static readonly JsonSerializerOptions Options = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
        };
    }
}
