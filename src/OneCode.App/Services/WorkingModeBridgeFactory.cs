using OneCode.App.Services.PlanMode;

namespace OneCode.App.Services;

public sealed class WorkingModeBridgeFactory(
    IPermissionModeProvider permissionModeProvider,
    IPlanModeService planMode,
    ILoggerFactory loggerFactory)
{
    public WorkingModeBridge Create(WorkingModeController controller) =>
        new(
            controller,
            permissionModeProvider,
            planMode,
            loggerFactory.CreateLogger<WorkingModeBridge>());
}
