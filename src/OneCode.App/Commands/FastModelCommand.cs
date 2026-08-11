using OneCode.Infrastructure.Config;
using CoreConstants = OneCode.Core.Constants;

namespace OneCode.App.Commands;

/// <summary>
/// 查看或设置快速模型（fastModel）。
/// fastModel 用于结构化 JSON 拆解、记忆提取、Hook 执行、下一步提示建议等轻量任务，未配置时回退到主模型。
/// 与 <c>/think</c>、<c>/model</c> 同模式：同时更新内存配置并落盘，当前会话立即生效。
/// </summary>
public sealed class FastModelCommand(IConfigManager config) : Command
{
    public override string Name => "fastmodel";
    public override string Description => "View or set the fast model (lightweight tasks)";
    public override CommandCategory Category => CommandCategory.Builtin;
    public override string? ArgumentHint => "[<id>|off]";

    public override async Task<CommandResult> ExecuteAsync(string[] args, CancellationToken ct = default)
    {
        var current = config.GetSetting<string>(CoreConstants.ConfigKeys.FastModel);

        if (args.Length == 0)
        {
            var display = string.IsNullOrEmpty(current)
                ? "(not configured, falls back to main model)"
                : current;
            return CommandResult.Text($"Fast model: {display}");
        }

        var value = args[0];
        // "off" / "none" / "" 表示关闭 fastModel，清空配置后回退主模型。
        if (value is "off" or "none")
            value = string.Empty;

        var mutation = string.IsNullOrEmpty(value)
            ? (ConfigMutation)new ConfigMutation.Remove()
            : new ConfigMutation.Set(value);
        var result = await config.ApplyAsync(
            new ConfigPatch(ConfigScope.User, new Dictionary<string, ConfigMutation>
            {
                [CoreConstants.ConfigKeys.FastModel] = mutation,
            }),
            ct).ConfigureAwait(false);
        if (!result.Saved)
            return CommandResult.Error(result.Error ?? "Failed to save fast model configuration.");

        return string.IsNullOrEmpty(value)
            ? CommandResult.Text("Fast model: cleared (will fall back to main model)")
            : CommandResult.Text($"Fast model set to: {value}");
    }
}
