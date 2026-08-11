namespace OneCode.Core.Permissions;

/// <summary>
/// Provides the current <see cref="PermissionMode"/> for the agent pipeline.
/// Runtime overrides (TUI /permissions) take precedence over persisted settings.
/// </summary>
public interface IPermissionModeProvider
{
    PermissionMode CurrentMode { get; }

    /// <summary>
    /// Sets a runtime override. Pass <c>null</c> to clear and fall back to config.
    /// </summary>
    void SetCurrentMode(PermissionMode? mode);
}
