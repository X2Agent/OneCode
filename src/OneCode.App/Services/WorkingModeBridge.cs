namespace OneCode.App.Services;

using OneCode.App.Services.PlanMode;
using OneCode.App.Tui;
using OneCode.Core.Permissions;

/// <summary>
/// Bridges the TUI <see cref="WorkingModeController"/> to
/// <see cref="IPermissionModeProvider"/> runtime overrides and
/// <see cref="IPlanModeService"/> enter/exit.
/// Created once per interactive session; dispose when the session ends.
/// </summary>
public sealed class WorkingModeBridge : IDisposable
{
    private readonly WorkingModeController _modeController;
    private readonly IPermissionModeProvider _permissionModeProvider;
    private readonly IPlanModeService _planMode;
    private readonly ILogger<WorkingModeBridge>? _logger;
    private PermissionMode? _prePlanAdvancedMode;
    private bool _disposed;

    public WorkingModeBridge(
        WorkingModeController modeController,
        IPermissionModeProvider permissionModeProvider,
        IPlanModeService planMode,
        ILogger<WorkingModeBridge>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(modeController);
        ArgumentNullException.ThrowIfNull(planMode);
        _modeController = modeController;
        _permissionModeProvider = permissionModeProvider;
        _planMode = planMode;
        _logger = logger;

        _modeController.ModeChanged += OnModeChanged;
    }

    private void OnModeChanged(object? sender, WorkingModeChangedEventArgs e)
    {
        try
        {
            // Preserve CLI-advanced permissions (e.g. BypassPermissions) through mode switches.
            // Plan mode is allowed to override because it has its own permission semantics.
            var currentMode = _permissionModeProvider.CurrentMode;
            if (IsCliAdvancedPermissionMode(currentMode) && e.CurrentMode != WorkingMode.Plan)
            {
                _logger?.LogDebug(
                    "WorkingModeBridge: skipping ApplyMode — CLI override active ({Mode})", currentMode);
                return;
            }

            ApplyMode(e.CurrentMode);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex,
                "Failed to bridge WorkingMode change to {Mode}", e.CurrentMode);
        }
    }

    /// <summary>
    /// Translates a UI <see cref="WorkingMode"/> into the corresponding
    /// backend state. Public so callers (e.g. tests, or an explicit
    /// re-sync after DI rebuild) can drive it deterministically.
    /// </summary>
    public void ApplyMode(WorkingMode mode)
    {
        switch (mode)
        {
            case WorkingMode.Plan:
                // Remember an advanced CLI override so leaving Plan restores it instead of
                // silently downgrading to AcceptEdits.
                var current = _permissionModeProvider.CurrentMode;
                _prePlanAdvancedMode = IsCliAdvancedPermissionMode(current) ? current : null;
                _planMode.EnterPlanMode();
                _logger?.LogDebug("WorkingModeBridge: entered PLAN mode");
                break;

            case WorkingMode.Team:
                ExitPlanIfNeeded();
                _prePlanAdvancedMode = null;
                _permissionModeProvider.SetCurrentMode(PermissionMode.Team);
                _logger?.LogDebug("WorkingModeBridge: entered TEAM mode (PermissionMode.Team)");
                break;

            case WorkingMode.Goal:
                ExitPlanIfNeeded();
                _prePlanAdvancedMode = null;
                _permissionModeProvider.SetCurrentMode(PermissionMode.GoalAuto);
                _logger?.LogDebug("WorkingModeBridge: entered GOAL mode (PermissionMode.GoalAuto)");
                break;

            case WorkingMode.Build:
                var restored = ExitPlanIfNeeded();
                var buildMode = restored is { } restoredMode && IsCliAdvancedPermissionMode(restoredMode)
                    ? restoredMode
                    : _prePlanAdvancedMode ?? PermissionMode.AcceptEdits;
                _prePlanAdvancedMode = null;
                _permissionModeProvider.SetCurrentMode(buildMode);
                _logger?.LogDebug("WorkingModeBridge: entered BUILD mode ({PermissionMode})", buildMode);
                break;
        }
    }

    private PermissionMode? ExitPlanIfNeeded()
    {
        return _planMode.IsInPlanMode ? _planMode.ExitPlanMode() : null;
    }

    /// <summary>
    /// Pushes the controller's <em>current</em> mode into the backend. Call
    /// this once after construction to synchronise state.
    /// </summary>
    public void SyncInitialState()
    {
        var currentMode = _permissionModeProvider.CurrentMode;

        if (IsCliAdvancedPermissionMode(currentMode))
        {
            _logger?.LogDebug(
                "WorkingModeBridge: skipping initial sync — CLI override active ({Mode})", currentMode);
            return;
        }

        ApplyMode(_modeController.Mode);
    }

    /// <summary>
    /// 判断指定的 <see cref="PermissionMode"/> 是否为 CLI 设置的高级权限模式。
    /// </summary>
    internal static bool IsCliAdvancedPermissionMode(PermissionMode? mode) =>
        mode is PermissionMode.BypassPermissions
            or PermissionMode.Auto
            or PermissionMode.DontAsk
            or PermissionMode.Bubble;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _modeController.ModeChanged -= OnModeChanged;
    }
}
