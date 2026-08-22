using OneCode.Infrastructure.Config;

namespace OneCode.App.Commands;

/// <summary>
/// /permissions — view or change the Build-mode tool-execution permission level.
///
/// 职责收拢后只做两件事：
/// 1. 持久化到 settings（ConfigManager）；
/// 2. 推送运行时 <see cref="IPermissionModeProvider"/>。
///
/// 工作模式（PLAN/TEAM/GOAL）与权限的联动由 <see cref="Services.WorkingModeBridge"/>
/// 统一桥接——本命令不再操作 WorkingModeController / IPlanModeService。
/// 此处设置的 Auto/DontAsk/Bubble/BypassPermissions 属于 CLI 高级档位，
/// WorkingModeBridge 会保护它们不被后续模式切换静默覆盖。
/// </summary>
public sealed class PermissionsCommand(
    IPermissionModeProvider modeProvider,
    IConfigManager config) : Command
{
    public override string Name => "permissions";
    public override string Description => "Manage tool execution permission levels (how strictly to review tool calls)";
    public override CommandCategory Category => CommandCategory.Builtin;
    public override string? ArgumentHint => "[mode]";

    private static new readonly Dictionary<string, PermissionMode> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["default"] = PermissionMode.Default,
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
        "default, auto, acceptEdits, bypassPermissions, dontAsk, bubble";

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

        var result = await config.ApplyAsync(
            ConfigPatch.Set(ConfigScope.User, OneCode.Core.Constants.ConfigKeys.PermissionMode, nm.ToString()),
            ct).ConfigureAwait(false);
        if (!result.Saved)
            return CommandResult.Error(result.Error ?? "Failed to save permission mode.");

        modeProvider.SetCurrentMode(nm);

        return CommandResult.Text($"Permission mode changed to: {nm}");
    }
}
