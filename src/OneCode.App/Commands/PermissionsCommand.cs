using OneCode.App.Services.PlanMode;
using OneCode.App.Tui;
using OneCode.Infrastructure.Config;

namespace OneCode.App.Commands;

/// <summary>
/// /permissions — view or change the tool-execution permission mode.
/// Updates <see cref="IPermissionModeProvider"/> (what the agent pipeline reads),
/// persists to settings, keeps plan-mode state in sync via <see cref="IPlanModeService"/>,
/// and syncs <see cref="WorkingModeController"/> so the UI reflects the runtime mode.
/// </summary>
public sealed class PermissionsCommand(
    IAppStateAccessor appState,
    IPermissionModeProvider modeProvider,
    IConfigManager config,
    IPlanModeService planMode,
    WorkingModeController modeController) : Command
{
    public override string Name => "permissions";
    public override string Description => "Manage tool execution permission modes (how strictly to review tool calls)";
    public override CommandCategory Category => CommandCategory.Builtin;
    public override string? ArgumentHint => "[mode]";

    private static new readonly Dictionary<string, PermissionMode> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["default"] = PermissionMode.Default,
        ["plan"] = PermissionMode.Plan,
        ["auto"] = PermissionMode.Auto,
        ["acceptedits"] = PermissionMode.AcceptEdits,
        ["accept-edits"] = PermissionMode.AcceptEdits,
        ["accept_edits"] = PermissionMode.AcceptEdits,
        ["bypasspermissions"] = PermissionMode.BypassPermissions,
        ["bypass-permissions"] = PermissionMode.BypassPermissions,
        ["bypass_permissions"] = PermissionMode.BypassPermissions,
        ["bypass"] = PermissionMode.BypassPermissions,
        ["dontask"] = PermissionMode.DontAsk,
        ["dont-ask"] = PermissionMode.DontAsk,
        ["dont_ask"] = PermissionMode.DontAsk,
        ["bubble"] = PermissionMode.Bubble,
    };

    private const string ValidModesHelp =
        "default, plan, auto, acceptEdits, bypassPermissions, dontAsk, bubble";

    public override async Task<CommandResult> ExecuteAsync(string[] args, CancellationToken ct = default)
    {
        if (args.Length == 0)
        {
            return CommandResult.Text(
                $"Permissions: Mode={modeProvider.CurrentMode}\nModes: {ValidModesHelp}");
        }

        if (!Aliases.TryGetValue(args[0], out var nm) && !Enum.TryParse(args[0], true, out nm))
            return CommandResult.Error(
                $"Unknown permission mode: '{args[0]}'\nValid: {ValidModesHelp}");

        // Set the requested mode before changing the UI controller. WorkingModeBridge
        // observes that event and must see the same authoritative permission state.
        var result = await config.ApplyAsync(
            ConfigPatch.Set(ConfigScope.User, OneCode.Core.Constants.ConfigKeys.PermissionMode, nm.ToString()),
            ct).ConfigureAwait(false);
        if (!result.Saved)
            return CommandResult.Error(result.Error ?? "Failed to save permission mode.");

        SyncPlanModeState(nm);
        modeProvider.SetCurrentMode(nm);

        // Sync WorkingModeController so the UI reflects the permission mode.
        if (nm == PermissionMode.Plan)
            modeController.Mode = WorkingMode.Plan;
        else if (modeController.Mode == WorkingMode.Plan)
            modeController.Mode = WorkingMode.Build;

        appState.Update(s => s with
        {
            ToolPermissionContext = s.ToolPermissionContext with { Mode = nm }
        });

        return CommandResult.Text($"Permission mode changed to: {nm}");
    }

    private void SyncPlanModeState(PermissionMode mode)
    {
        if (mode == PermissionMode.Plan)
            planMode.EnterPlanMode();
        else if (planMode.IsInPlanMode)
            planMode.ExitPlanMode();
    }
}
