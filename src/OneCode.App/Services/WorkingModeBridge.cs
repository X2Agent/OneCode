namespace OneCode.App.Services;

using OneCode.App.Services.PlanMode;
using OneCode.Core.Permissions;

/// <summary>
/// 把 TUI 的 <see cref="WorkingModeController"/> 桥接到
/// <see cref="IPermissionModeProvider"/> 的运行时权限覆盖与
/// <see cref="IPlanModeService"/> 的进入/退出。
/// 每个交互式会话创建一份；会话结束时释放。
/// </summary>
public sealed class WorkingModeBridge : IDisposable
{
    private readonly WorkingModeController _modeController;
    private readonly IPermissionModeProvider _permissionModeProvider;
    private readonly IPlanModeService _planMode;
    private readonly ILogger<WorkingModeBridge>? _logger;
    private PermissionMode? _preGoalMode;
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
            // 命令行参数设置的高级权限档（如 BypassPermissions）在模式切换中保持不变；
            // Plan 模式允许覆盖，因为它有独立的权限语义。
            var currentMode = _permissionModeProvider.CurrentMode;
            if (IsCommandLineAdvancedPermissionMode(currentMode) && e.CurrentMode != WorkingMode.Plan)
            {
                _logger?.LogDebug(
                    "WorkingModeBridge: skipping ApplyMode — command-line override active ({Mode})", currentMode);
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
    /// 把 UI 的 <see cref="WorkingMode"/> 翻译为对应的后端状态。设为 public 以便
    /// 调用方（如测试或 DI 重建后的显式再同步）确定性地驱动。
    /// </summary>
    public void ApplyMode(WorkingMode mode)
    {
        switch (mode)
        {
            case WorkingMode.Plan:
                // PlanModeService.EnterPlanMode() 自身作为进入 Plan 前权限模式的唯一事实源。
                _planMode.EnterPlanMode();
                _logger?.LogDebug("WorkingModeBridge: entered PLAN mode");
                break;

            case WorkingMode.Goal:
                var restoredBeforeGoal = ExitPlanIfNeeded();
                var currentBeforeGoal = restoredBeforeGoal ?? _permissionModeProvider.CurrentMode;
                if (IsBuildCompatiblePermissionMode(currentBeforeGoal))
                    _preGoalMode = currentBeforeGoal;
                _permissionModeProvider.SetCurrentMode(PermissionMode.GoalAuto);
                _logger?.LogDebug("WorkingModeBridge: entered GOAL mode (PermissionMode.GoalAuto)");
                break;

            case WorkingMode.Build:
                var restored = ExitPlanIfNeeded();
                var preGoal = _preGoalMode;
                _preGoalMode = null;
                var current = _permissionModeProvider.CurrentMode;
                var buildMode = IsBuildCompatiblePermissionMode(restored)
                    ? restored!.Value
                    : IsBuildCompatiblePermissionMode(preGoal)
                        ? preGoal!.Value
                        : IsBuildCompatiblePermissionMode(current)
                            ? current
                            : PermissionMode.Default;
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
    /// 把控制器的当前模式推送进后端。构造完成后调用一次以同步状态。
    /// </summary>
    public void SyncInitialState()
    {
        var currentMode = _permissionModeProvider.CurrentMode;

        if (IsCommandLineAdvancedPermissionMode(currentMode))
        {
            _logger?.LogDebug(
                "WorkingModeBridge: skipping initial sync — command-line override active ({Mode})", currentMode);
            return;
        }

        ApplyMode(_modeController.Mode);
    }

    /// <summary>
    /// 判断指定的 <see cref="PermissionMode"/> 是否为命令行参数设置的高级权限模式。
    /// </summary>
    internal static bool IsCommandLineAdvancedPermissionMode(PermissionMode? mode) =>
        mode is PermissionMode.BypassPermissions
            or PermissionMode.Auto
            or PermissionMode.DontAsk;

    /// <summary>
    /// 判断指定的 <see cref="PermissionMode"/> 是否为可直接在 BUILD 模式生效的用户权限档位
    /// （排除由工作模式派生的 <see cref="PermissionMode.Plan"/> 与 <see cref="PermissionMode.GoalAuto"/>）。
    /// </summary>
    private static bool IsBuildCompatiblePermissionMode(PermissionMode? mode) =>
        mode is PermissionMode.Default
            or PermissionMode.AcceptEdits
            or PermissionMode.BypassPermissions
            or PermissionMode.Auto
            or PermissionMode.DontAsk;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _modeController.ModeChanged -= OnModeChanged;
    }
}