using OneCode.Core.Config;

namespace OneCode.App.Commands;

/// <summary>
/// /permissions — view or change the Build-mode tool-execution permission level.
///
/// 职责收拢后只做三件事：
/// 1. 无参时展示当前生效档位 + 持久化配置值；
/// 2. 持久化到 settings（ConfigManager）；
/// 3. 推送运行时 <see cref="IPermissionModeProvider"/>。
///
/// 追加 <c>--session</c>（别名 <c>--once</c>）时跳过持久化，仅设运行时覆盖——
/// 当前对话内全放行（bypass）等一次性配置不应泄漏到之后的会话。
///
/// 工作模式（PLAN/TEAM/GOAL）与权限的联动由 <see cref="Services.WorkingModeBridge"/>
/// 统一桥接——本命令不再操作 WorkingModeController / IPlanModeService。
/// plan / team / goalAuto 是工作模式派生的权限策略而非用户档位，直接写入会造成
/// 权限轴与模式轴不一致的半状态，故本命令显式拒绝（不用 Enum.TryParse 兜底放行）。
/// 此处设置的 Auto/DontAsk/BypassPermissions 属于 CLI 高级档位，
/// WorkingModeBridge 会保护它们不被后续模式切换静默覆盖。
/// </summary>
public sealed class PermissionsCommand(
    IPermissionModeProvider modeProvider,
    IConfigManager config) : Command
{
    public override string Name => "permissions";
    public override string Description => "Manage tool execution permission levels (how strictly to review tool calls)";
    public override CommandCategory Category => CommandCategory.Builtin;
    public override string? ArgumentHint => "[mode] [--session]";

    // 用户可直接设置的档位白名单（单一事实源）。刻意不做 Enum.TryParse 兜底：
    // plan / team / goalAuto 只能由 WorkingModeBridge 从工作模式派生。
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
    };

    /// <summary>工作模式派生的权限策略——不是用户档位，切换请输入对应工作模式。</summary>
    private static readonly Dictionary<string, WorkingMode[]> DerivedModes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["plan"] = [WorkingMode.Plan],
        ["team"] = [WorkingMode.Team],
        ["goalauto"] = [WorkingMode.Goal],
        ["goal-auto"] = [WorkingMode.Goal],
        ["goal_auto"] = [WorkingMode.Goal],
    };

    private const string ValidModesHelp =
        "default, auto, acceptEdits, bypassPermissions, dontAsk";

    /// <summary>仅当前对话生效、不写配置的作用域标志。</summary>
    private static readonly HashSet<string> SessionFlags =
        new(StringComparer.Ordinal) { "--session", "--once" };

    public override async Task<CommandResult> ExecuteAsync(string[] args, CancellationToken ct = default)
    {
        string? modeArg = null;
        var sessionOnly = false;
        foreach (var arg in args)
        {
            if (SessionFlags.Contains(arg))
            {
                sessionOnly = true;
                continue;
            }
            if (arg.StartsWith("--", StringComparison.Ordinal))
                return CommandResult.Error($"Unknown option: '{arg}'\nValid: {ValidModesHelp} [--session]");
            if (modeArg is not null)
                return CommandResult.Error($"Unexpected argument: '{arg}'\nUsage: /permissions [mode] [--session]");
            modeArg = arg;
        }

        if (modeArg is null)
        {
            if (sessionOnly)
                return CommandResult.Error("--session requires a mode.\nUsage: /permissions [mode] [--session]");
            return CommandResult.Text(FormatStatus());
        }

        if (!Aliases.TryGetValue(modeArg, out var mode))
        {
            if (DerivedModes.TryGetValue(modeArg, out var modes))
            {
                var switchHint = string.Join(" / ", modes.Select(m => m.ToString().ToUpperInvariant()));
                return CommandResult.Error(
                    $"'{modeArg}' is a working-mode-derived permission policy, not a permission level. " +
                    $"Switch to {switchHint} mode instead (Tab or Alt+1..4).");
            }
            return CommandResult.Error(
                $"Unknown permission mode: '{modeArg}'\nValid: {ValidModesHelp}");
        }

        if (!sessionOnly)
        {
            var result = await config.ApplyAsync(
                ConfigPatch.Set(ConfigScope.User, OneCode.Core.Constants.ConfigKeys.PermissionMode, mode.ToString()),
                ct).ConfigureAwait(false);
            if (!result.Saved)
                return CommandResult.Error(result.Error ?? "Failed to save permission mode.");
        }

        modeProvider.SetCurrentMode(mode);

        var scope = sessionOnly ? " (session only, not persisted)" : "";
        return CommandResult.Text($"Permission mode changed to: {mode}{scope}");
    }

    /// <summary>
    /// 无参状态行：当前生效档位 + 持久化配置值（两者不同说明有运行时覆盖活跃），
    /// 并提示 --session 用法。
    /// </summary>
    private string FormatStatus()
    {
        var current = modeProvider.CurrentMode;
        var persisted = config.Current.Effective.PermissionMode;
        var overrideNote = string.Equals(persisted, current.ToString(), StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : $" (config: {persisted})";
        return $"Permissions: Mode={current}{overrideNote}\n" +
            $"Modes: {ValidModesHelp}\n" +
            "Tip: append --session to apply a mode for this conversation only (not persisted).";
    }
}
