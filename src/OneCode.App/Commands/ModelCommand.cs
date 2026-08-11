using System.Text;
using OneCode.Core.Models;
using OneCode.Infrastructure.Config;

namespace OneCode.App.Commands;

public sealed class ModelCommand(
    IAppStateAccessor appState,
    IConfigManager config,
    IModelManager modelManager) : Command
{
    public override string Name => "model";
    public override string Description => "View or change the current main model";
    public override CommandCategory Category => CommandCategory.Builtin;
    public override string? ArgumentHint => "[<id>]";

    public override async Task<CommandResult> ExecuteAsync(string[] args, CancellationToken ct = default)
    {
        var effectiveModel = appState.Current.MainLoopModel
            ?? config.Current.Effective.Model;

        if (args.Length == 0)
        {
            if (string.IsNullOrEmpty(effectiveModel))
                return CommandResult.Text(
                    "当前未配置模型。请通过以下方式配置：\n" +
                    "  /model <模型ID>（如 /model claude-sonnet-4-6）\n" +
                    "  /config 打开配置面板\n" +
                    "  在 settings.json 中设置 \"model\" 字段");

            var available = modelManager.GetAll();
            var sb = new StringBuilder();
            sb.AppendLine(CultureInfo.InvariantCulture, $"Current model: {effectiveModel}");

            if (available.Count > 1)
            {
                sb.AppendLine("Available models:");
                foreach (var m in available)
                    sb.AppendLine(ReferenceEquals(m, modelManager.GetDefault())
                        ? $"  * {m.Id} (default)"
                        : $"  - {m.Id}");
            }

            return CommandResult.Text(sb.ToString().TrimEnd());
        }

        var newModel = args[0];
        var resolved = modelManager.Resolve(newModel);
        newModel = resolved?.Id ?? newModel;

        var result = await config.ApplyAsync(
            ConfigPatch.Set(ConfigScope.User, OneCode.Core.Constants.ConfigKeys.Model, newModel),
            ct).ConfigureAwait(false);
        if (!result.Saved)
            return CommandResult.Error(result.Error ?? "Failed to save model configuration.");

        appState.Update(s => s with { MainLoopModel = newModel });
        return CommandResult.Text($"Model changed to: {newModel} (applies to the next operation)");
    }
}
