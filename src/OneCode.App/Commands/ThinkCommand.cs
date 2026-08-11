using OneCode.Infrastructure.Config;

namespace OneCode.App.Commands;

/// <summary>
/// 思考配置命令——管理两个独立维度：
///
/// 1. 模型思考开关 + reasoning_effort（努力程度）：
///    - /think on|off — 开关模型扩展思考（thinkingEnabled）
///    - /think low|medium|high|max — 设置 reasoning_effort（同时自动开启思考）
///
/// 2. TUI 思考过程显示：
///    - /think show|hide — 控制对话历史中思考块是否展开显示（showThinking）
///
/// 无参数时显示当前状态。
/// </summary>
public sealed class ThinkCommand(IAppStateAccessor appState, IConfigManager config) : Command
{
    public override string Name => "think";
    public override string Description => "Configure thinking: on/off, effort level, or TUI display";
    public override CommandCategory Category => CommandCategory.Builtin;
    public override string? ArgumentHint => "[on|off|low|medium|high|max|show|hide]";

    private static readonly HashSet<string> EffortLevels = ["low", "medium", "high", "max"];

    public override async Task<CommandResult> ExecuteAsync(string[] args, CancellationToken ct = default)
    {
        if (args.Length == 0)
        {
            var s = appState.Current;
            var show = config.Current.Effective.Get("showThinking", false);
            return CommandResult.Text(
                $"模型思考:     {(s.ThinkingEnabled ? "ON" : "OFF")} (effort: {s.EffortValue.ToString().ToLowerInvariant()})\n" +
                $"TUI 显示:      {(show ? "show" : "hide")}");
        }

        var arg = args[0].ToLowerInvariant();
        if (arg == "on") return await SetThinkingEnabled(true, ct);
        if (arg == "off") return await SetThinkingEnabled(false, ct);
        if (arg == "show") return await SetShowThinking(true, ct);
        if (arg == "hide") return await SetShowThinking(false, ct);
        if (EffortLevels.Contains(arg)) return await SetEffort(arg, ct);

        return CommandResult.Error(
            $"Unknown argument '{args[0]}'. Valid: on|off|low|medium|high|max|show|hide");
    }

    // 维度1a：开关模型思考

    private async Task<CommandResult> SetThinkingEnabled(bool enabled, CancellationToken ct)
    {
        var result = await config.ApplyAsync(
            ConfigPatch.Set(ConfigScope.User, "thinkingEnabled", enabled),
            ct).ConfigureAwait(false);
        if (!result.Saved)
            return CommandResult.Error(result.Error ?? "保存思考配置失败。");

        appState.Update(s => s with { ThinkingEnabled = enabled });
        return CommandResult.Text($"模型思考: {(enabled ? "ON" : "OFF")} (effort: {appState.Current.EffortValue.ToString().ToLowerInvariant()}，下次操作生效)");
    }

    // 维度1b：设置 reasoning_effort（同时自动开启思考）

    private async Task<CommandResult> SetEffort(string effort, CancellationToken ct)
    {
        var result = await config.ApplyAsync(
            new ConfigPatch(ConfigScope.User, new Dictionary<string, ConfigMutation>
            {
                ["effortValue"] = new ConfigMutation.Set(effort),
                ["thinkingEnabled"] = new ConfigMutation.Set(true),
            }),
            ct).ConfigureAwait(false);
        if (!result.Saved)
            return CommandResult.Error(result.Error ?? "保存思考配置失败。");

        var effortLevel = EffortThinking.ParseEffort(effort);
        appState.Update(s => s with { EffortValue = effortLevel, ThinkingEnabled = true });
        return CommandResult.Text($"模型思考: ON (effort: {effort}，下次操作生效)");
    }

    // 维度2：TUI 思考过程显示

    private async Task<CommandResult> SetShowThinking(bool show, CancellationToken ct)
    {
        var result = await config.ApplyAsync(
            ConfigPatch.Set(ConfigScope.User, "showThinking", show),
            ct).ConfigureAwait(false);
        if (!result.Saved)
            return CommandResult.Error(result.Error ?? "保存思考显示配置失败。");

        appState.Update(s => s with { ShowThinking = show });
        return CommandResult.Text($"TUI 思考显示: {(show ? "SHOW (展开)" : "HIDE (折叠)")}（立即生效）");
    }
}
