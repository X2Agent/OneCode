using CoreConstants = OneCode.Core.Constants;

namespace OneCode.Core.Config;

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
    public long MaxBudgetTokens { get => Get(CoreConstants.ConfigKeys.MaxBudgetTokens, CoreConstants.Session.MaxBudgetTokensDefault); init => _values[CoreConstants.ConfigKeys.MaxBudgetTokens] = value; }
    public string PermissionMode { get => Get(CoreConstants.ConfigKeys.PermissionMode, CoreConstants.PermissionModes.Default) ?? CoreConstants.PermissionModes.Default; init => _values[CoreConstants.ConfigKeys.PermissionMode] = value; }
    public bool NextPromptSuggesterEnabled { get => Get(CoreConstants.ConfigKeys.NextPromptSuggesterEnabled, true); init => _values[CoreConstants.ConfigKeys.NextPromptSuggesterEnabled] = value; }
    public bool NotificationsEnabled { get => Get(CoreConstants.ConfigKeys.NotificationsEnabled, false); init => _values[CoreConstants.ConfigKeys.NotificationsEnabled] = value; }
    public int OllamaContextWindow { get => Get(CoreConstants.ConfigKeys.OllamaContextWindow, 32_768); init => _values[CoreConstants.ConfigKeys.OllamaContextWindow] = value; }
    /// <summary>
    /// WebSearch 主提供方（<see cref="CoreConstants.ConfigKeys.WebSearchProvider"/>）：
    /// <c>tavily</c> 或 <c>duckduckgo</c>。决定搜索链路首选提供者，失败时自动回退另一提供方。
    /// </summary>
    public string WebSearchProvider { get => Get(CoreConstants.ConfigKeys.WebSearchProvider, "duckduckgo") ?? "duckduckgo"; init => _values[CoreConstants.ConfigKeys.WebSearchProvider] = value; }
    /// <summary>Tavily 搜索 API Key（<see cref="CoreConstants.ConfigKeys.TavilyApiKey"/>）。</summary>
    public string? TavilyApiKey { get => Get<string>(CoreConstants.ConfigKeys.TavilyApiKey); init => _values[CoreConstants.ConfigKeys.TavilyApiKey] = value; }
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
