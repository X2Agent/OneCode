namespace OneCode.App.Services;

using OneCode.Core.Permissions;
using OneCode.Infrastructure.Config;

/// <summary>
/// Provides the current <see cref="PermissionMode"/> for the agent pipeline.
/// Reads settings by default; a runtime override (TUI WorkingModeBridge or /permissions)
/// takes precedence so mode switches apply immediately without relying on AppState alone.
/// </summary>
/// <remarks>
/// Runtime override via <see cref="SetCurrentMode"/> is intentional session-scoped state
/// (TUI / slash-command), not a DI ambient anti-pattern.
/// </remarks>
public sealed class PermissionModeProvider : IPermissionModeProvider
{
    private readonly IConfigManager _config;
    private readonly ILogger<PermissionModeProvider>? _logger;

    // Runtime override — null means "fall back to config".
    // Guarded by a lock because Nullable<enum> cannot be marked volatile
    // (CS0677) and the field is written by the UI thread and read by
    // tool-execution threads.
    private readonly object _runtimeOverrideLock = new();
    private PermissionMode? _runtimeOverride;

    public PermissionModeProvider(IConfigManager config, ILogger<PermissionModeProvider>? logger = null)
    {
        _config = config;
        _logger = logger;
    }

    public PermissionMode CurrentMode
    {
        get
        {
            // Runtime override wins — set by WorkingModeBridge when the user
            // switches modes in the TUI (e.g. pressing 2 for PLAN).
            PermissionMode? overrideMode;
            lock (_runtimeOverrideLock) overrideMode = _runtimeOverride;
            if (overrideMode is { } om)
                return om;

            var modeStr = _config.Current.Effective.PermissionMode;
            if (Enum.TryParse<PermissionMode>(modeStr, ignoreCase: true, out var mode))
                return mode;

            _logger?.LogWarning(
                "Unknown permission mode '{Mode}', falling back to Default", modeStr);
            return PermissionMode.Default;
        }
    }

    /// <summary>
    /// Sets a runtime override for the current permission mode. Pass <c>null</c>
    /// to clear the override and fall back to <see cref="ConfigManager"/>.
    /// </summary>
    public void SetCurrentMode(PermissionMode? mode)
    {
        PermissionMode? previous;
        lock (_runtimeOverrideLock)
        {
            previous = _runtimeOverride;
            _runtimeOverride = mode;
        }
        if (previous != mode)
        {
            _logger?.LogInformation(
                "PermissionMode runtime override: {Previous} -> {Current}", previous, mode);
        }
    }
}
