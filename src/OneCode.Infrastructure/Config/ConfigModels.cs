namespace OneCode.Infrastructure.Config;

public enum ConfigScope
{
    BuiltIn,
    User,
    Project,
    Environment,
    Session,
}

public enum ActivationMode
{
    Immediate,
    NextOperation,
    RestartRequired,
}

public enum ConfigValueKind
{
    String,
    Boolean,
    Int32,
    Int64,
    Double,
    Decimal,
    StringList,
}

public sealed record SettingDescriptor(
    string Key,
    ActivationMode Activation,
    ConfigValueKind ValueKind = ConfigValueKind.String,
    bool IsSecret = false,
    bool AllowProjectScope = true,
    string? EnvironmentVariable = null,
    object? BuiltInDefault = null);

public sealed record ConfigValueInfo(
    object? Value,
    ConfigScope Source,
    bool IsOverridden);

public sealed record ConfigSnapshot(
    AppSettings Effective,
    IReadOnlyDictionary<string, ConfigValueInfo> Values,
    IReadOnlyDictionary<ConfigScope, IReadOnlyDictionary<string, object?>> ScopedValues)
{
    public ConfigValueInfo GetValueInfo(string key) =>
        Values.TryGetValue(key, out var info)
            ? info
            : new ConfigValueInfo(null, ConfigScope.BuiltIn, false);

    public object? GetScopedValue(ConfigScope scope, string key) =>
        ScopedValues.TryGetValue(scope, out var values) && values.TryGetValue(key, out var value)
            ? value
            : null;

    public bool HasScopedValue(ConfigScope scope, string key) =>
        ScopedValues.TryGetValue(scope, out var values) && values.ContainsKey(key);

    public static ConfigSnapshot FromEffective(AppSettings effective, ConfigScope source = ConfigScope.User)
    {
        ArgumentNullException.ThrowIfNull(effective);
        var values = effective.ToDictionary();
        var infos = SettingDescriptors.All.ToDictionary(
            descriptor => descriptor.Key,
            descriptor => new ConfigValueInfo(
                values.TryGetValue(descriptor.Key, out var value) ? value : null,
                source,
                false),
            StringComparer.OrdinalIgnoreCase);
        var scoped = Enum.GetValues<ConfigScope>().ToDictionary(
            scope => scope,
            scope => (IReadOnlyDictionary<string, object?>)(scope == source
                ? new Dictionary<string, object?>(values, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)));
        return new ConfigSnapshot(effective, infos, scoped);
    }
}

public abstract record ConfigMutation
{
    private ConfigMutation()
    {
    }

    public sealed record Set(object? Value) : ConfigMutation;

    public sealed record Remove : ConfigMutation;
}

public sealed record ConfigPatch(
    ConfigScope TargetScope,
    IReadOnlyDictionary<string, ConfigMutation> Changes)
{
    public static ConfigPatch Set(ConfigScope scope, string key, object? value) =>
        new(scope, new Dictionary<string, ConfigMutation>(StringComparer.OrdinalIgnoreCase)
        {
            [key] = new ConfigMutation.Set(value),
        });
}

public sealed record ConfigApplyResult(
    bool Saved,
    ConfigSnapshot Snapshot,
    IReadOnlyList<string> ImmediateChanges,
    IReadOnlyList<string> NextOperationChanges,
    IReadOnlyList<string> RestartRequiredChanges,
    IReadOnlyList<string> OverriddenChanges,
    string? Error = null)
{
    public static ConfigApplyResult Failure(ConfigSnapshot snapshot, string error) =>
        new(false, snapshot, [], [], [], [], error);
}

public static class SettingDescriptors
{
    private static readonly IReadOnlyDictionary<string, SettingDescriptor> Items =
        new Dictionary<string, SettingDescriptor>(StringComparer.OrdinalIgnoreCase)
        {
            ["apiKey"] = new("apiKey", ActivationMode.RestartRequired, IsSecret: true, EnvironmentVariable: "ONECODE_API_KEY"),
            ["baseUrl"] = new("baseUrl", ActivationMode.RestartRequired, EnvironmentVariable: "ONECODE_BASE_URL"),
            ["provider"] = new("provider", ActivationMode.RestartRequired, EnvironmentVariable: "ONECODE_PROVIDER_OVERRIDE", BuiltInDefault: "anthropic"),
            ["model"] = new("model", ActivationMode.NextOperation, EnvironmentVariable: "ONECODE_MODEL"),
            ["fastModel"] = new("fastModel", ActivationMode.NextOperation),
            ["permissionMode"] = new("permissionMode", ActivationMode.NextOperation, BuiltInDefault: "default"),
            ["trustedDirectories"] = new("trustedDirectories", ActivationMode.Immediate, ConfigValueKind.StringList, AllowProjectScope: false, BuiltInDefault: Array.Empty<string>()),
            ["allowedDirectories"] = new("allowedDirectories", ActivationMode.Immediate, ConfigValueKind.StringList, BuiltInDefault: Array.Empty<string>()),
            ["hasTrustAccepted"] = new("hasTrustAccepted", ActivationMode.Immediate, ConfigValueKind.Boolean, AllowProjectScope: false, BuiltInDefault: false),
            ["maxTurns"] = new("maxTurns", ActivationMode.NextOperation, ConfigValueKind.Int32, BuiltInDefault: 100),
            ["maxBudgetUsd"] = new("maxBudgetUsd", ActivationMode.NextOperation, ConfigValueKind.Double, BuiltInDefault: 10.0),
            ["webSearchProvider"] = new("webSearchProvider", ActivationMode.NextOperation, EnvironmentVariable: "ONECODE_WEB_SEARCH_PROVIDER", BuiltInDefault: "duckduckgo"),
            ["webSearchApiKey"] = new("webSearchApiKey", ActivationMode.NextOperation, IsSecret: true, EnvironmentVariable: "ONECODE_WEB_SEARCH_API_KEY"),
            ["thinkingEnabled"] = new("thinkingEnabled", ActivationMode.NextOperation, ConfigValueKind.Boolean, BuiltInDefault: false),
            ["effortValue"] = new("effortValue", ActivationMode.NextOperation, BuiltInDefault: "medium"),
            ["showThinking"] = new("showThinking", ActivationMode.Immediate, ConfigValueKind.Boolean, BuiltInDefault: false),
            ["nextPromptSuggesterEnabled"] = new("nextPromptSuggesterEnabled", ActivationMode.NextOperation, ConfigValueKind.Boolean, BuiltInDefault: true),
            ["notificationsEnabled"] = new("notificationsEnabled", ActivationMode.NextOperation, ConfigValueKind.Boolean, BuiltInDefault: false),
            ["ollamaContextWindow"] = new("ollamaContextWindow", ActivationMode.RestartRequired, ConfigValueKind.Int32, BuiltInDefault: 32_768),
            ["autodream.enabled"] = new("autodream.enabled", ActivationMode.NextOperation, ConfigValueKind.Boolean, EnvironmentVariable: "ONECODE_AUTODREAM", BuiltInDefault: true),
            ["autodream.minHours"] = new("autodream.minHours", ActivationMode.NextOperation, ConfigValueKind.Int32, EnvironmentVariable: "ONECODE_AUTODREAM_MIN_HOURS", BuiltInDefault: 6),
            ["autodream.minSessions"] = new("autodream.minSessions", ActivationMode.NextOperation, ConfigValueKind.Int32, EnvironmentVariable: "ONECODE_AUTODREAM_MIN_SESSIONS", BuiltInDefault: 3),
            ["goal.maxSubGoalAttempts"] = new("goal.maxSubGoalAttempts", ActivationMode.NextOperation, ConfigValueKind.Int32, BuiltInDefault: 20),
            ["goal.maxTurnsPerSubGoal"] = new("goal.maxTurnsPerSubGoal", ActivationMode.NextOperation, ConfigValueKind.Int32, BuiltInDefault: 50),
            ["goal.maxTotalTokens"] = new("goal.maxTotalTokens", ActivationMode.NextOperation, ConfigValueKind.Int64, BuiltInDefault: 200_000L),
            ["goal.maxWallClockHours"] = new("goal.maxWallClockHours", ActivationMode.NextOperation, ConfigValueKind.Double, BuiltInDefault: 2.0),
            ["goal.maxCostUsd"] = new("goal.maxCostUsd", ActivationMode.NextOperation, ConfigValueKind.Decimal, BuiltInDefault: 5.0m),
        };

    public static IEnumerable<SettingDescriptor> All => Items.Values;

    public static bool TryGet(string key, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out SettingDescriptor? descriptor) =>
        Items.TryGetValue(key, out descriptor);

    public static SettingDescriptor Get(string key) =>
        Items.TryGetValue(key, out var descriptor)
            ? descriptor
            : throw new ArgumentException($"Unknown configuration key '{key}'.", nameof(key));

    public static object? Coerce(SettingDescriptor descriptor, object? value)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (value is null)
            return null;

        try
        {
            return descriptor.ValueKind switch
            {
                ConfigValueKind.String => Convert.ToString(value, CultureInfo.InvariantCulture),
                ConfigValueKind.Boolean => Convert.ToBoolean(value, CultureInfo.InvariantCulture),
                ConfigValueKind.Int32 => Convert.ToInt32(value, CultureInfo.InvariantCulture),
                ConfigValueKind.Int64 => Convert.ToInt64(value, CultureInfo.InvariantCulture),
                ConfigValueKind.Double => Convert.ToDouble(value, CultureInfo.InvariantCulture),
                ConfigValueKind.Decimal => Convert.ToDecimal(value, CultureInfo.InvariantCulture),
                ConfigValueKind.StringList => value switch
                {
                    string text => text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                    IEnumerable<string> strings => strings.ToArray(),
                    IEnumerable<object?> objects => objects
                        .Select(item => Convert.ToString(item, CultureInfo.InvariantCulture))
                        .OfType<string>()
                        .ToArray(),
                    _ => throw new InvalidCastException(),
                },
                _ => value,
            };
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
        {
            throw new FormatException(
                $"Configuration key '{descriptor.Key}' has an invalid {descriptor.ValueKind} value.",
                ex);
        }
    }
}
