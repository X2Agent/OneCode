using System.Text;
using OneCode.Infrastructure.Config;
using CoreConstants = OneCode.Core.Constants;

namespace OneCode.App.Commands;

/// <summary>
/// 查看有效配置、来源和生效模式，并通过显式作用域 Patch 修改配置。
/// </summary>
public sealed class ConfigCommand(
    IConfigManager configManager,
    IAppStateAccessor appStateAccessor) : Command
{
    public override string Name => "config";
    public override string Description => "View or edit scoped configuration";
    public override CommandCategory Category => CommandCategory.Builtin;
    public override string? ArgumentHint => "[list|get <key>|set <user|project|session> <key> <value>|remove <user|project|session> <key>]";

    public override async Task<CommandResult> ExecuteAsync(string[] args, CancellationToken ct = default)
    {
        if (args.Length == 0 || args[0] is "list" or "ls")
            return CommandResult.Text(ListConfig());

        return args[0].ToLowerInvariant() switch
        {
            "get" when args.Length == 2 => GetConfig(args[1]),
            "set" when args.Length >= 4 => await SetConfigAsync(
                ParseScope(args[1]),
                args[2],
                string.Join(" ", args[3..]),
                ct).ConfigureAwait(false),
            "remove" when args.Length == 3 => await RemoveConfigAsync(
                ParseScope(args[1]),
                args[2],
                ct).ConfigureAwait(false),
            _ => CommandResult.Error(
                "Usage: /config [list|get <key>|set <user|project|session> <key> <value>|remove <user|project|session> <key>]")
        };
    }

    private string ListConfig()
    {
        var snapshot = configManager.Current;
        var builder = new StringBuilder("Configuration:\n");
        foreach (var descriptor in SettingDescriptors.All.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase))
        {
            var info = snapshot.GetValueInfo(descriptor.Key);
            builder.Append("  ")
                .Append(descriptor.Key)
                .Append(" = ")
                .Append(descriptor.IsSecret && info.Value is not null ? "****" : FormatValue(info.Value))
                .Append(" (source: ")
                .Append(info.Source.ToString().ToLowerInvariant())
                .Append(", activation: ")
                .Append(ToDisplayName(descriptor.Activation));
            if (info.IsOverridden)
                builder.Append(", overrides lower scope");
            builder.AppendLine(")");
        }
        return builder.ToString().TrimEnd();
    }

    private CommandResult GetConfig(string key)
    {
        if (!SettingDescriptors.TryGet(key, out var descriptor))
            return CommandResult.Error($"Unknown configuration key '{key}'.");

        var info = configManager.Current.GetValueInfo(descriptor.Key);
        var value = descriptor.IsSecret && info.Value is not null ? "****" : FormatValue(info.Value);
        return CommandResult.Text(
            $"{descriptor.Key} = {value} (source: {info.Source.ToString().ToLowerInvariant()}, activation: {ToDisplayName(descriptor.Activation)})");
    }

    private async Task<CommandResult> SetConfigAsync(
        ConfigScope scope,
        string key,
        string rawValue,
        CancellationToken ct)
    {
        if (!SettingDescriptors.TryGet(key, out var descriptor))
            return CommandResult.Error($"Unknown configuration key '{key}'.");

        object? value;
        try
        {
            value = SettingDescriptors.Coerce(descriptor, rawValue);
        }
        catch (FormatException ex)
        {
            return CommandResult.Error(ex.Message);
        }
        var result = await configManager.ApplyAsync(
            ConfigPatch.Set(scope, descriptor.Key, value),
            ct).ConfigureAwait(false);
        if (!result.Saved)
            return CommandResult.Error(result.Error ?? "Failed to save configuration.");

        ApplyRuntimeState(descriptor.Key, value);
        return CommandResult.Text(BuildApplyMessage("Set", scope, descriptor, result));
    }

    private async Task<CommandResult> RemoveConfigAsync(
        ConfigScope scope,
        string key,
        CancellationToken ct)
    {
        if (!SettingDescriptors.TryGet(key, out var descriptor))
            return CommandResult.Error($"Unknown configuration key '{key}'.");

        var result = await configManager.ApplyAsync(
            new ConfigPatch(scope, new Dictionary<string, ConfigMutation>
            {
                [descriptor.Key] = new ConfigMutation.Remove(),
            }),
            ct).ConfigureAwait(false);
        if (!result.Saved)
            return CommandResult.Error(result.Error ?? "Failed to remove configuration override.");

        ApplyRuntimeState(descriptor.Key, result.Snapshot.GetValueInfo(descriptor.Key).Value);
        return CommandResult.Text(BuildApplyMessage("Removed override for", scope, descriptor, result));
    }

    private void ApplyRuntimeState(string key, object? value)
    {
        if (string.Equals(key, CoreConstants.ConfigKeys.Model, StringComparison.OrdinalIgnoreCase))
        {
            appStateAccessor.Update(state => state with { MainLoopModel = Convert.ToString(value, CultureInfo.InvariantCulture) });
        }
        else if (string.Equals(key, "thinkingEnabled", StringComparison.OrdinalIgnoreCase))
        {
            appStateAccessor.Update(state => state with { ThinkingEnabled = Convert.ToBoolean(value, CultureInfo.InvariantCulture) });
        }
        else if (string.Equals(key, "showThinking", StringComparison.OrdinalIgnoreCase))
        {
            appStateAccessor.Update(state => state with { ShowThinking = Convert.ToBoolean(value, CultureInfo.InvariantCulture) });
        }
        else if (string.Equals(key, "effortValue", StringComparison.OrdinalIgnoreCase))
        {
            appStateAccessor.Update(state => state with
            {
                EffortValue = EffortThinking.ParseEffort(Convert.ToString(value, CultureInfo.InvariantCulture) ?? "medium"),
            });
        }
    }

    private static ConfigScope ParseScope(string value) => value.ToLowerInvariant() switch
    {
        "user" => ConfigScope.User,
        "project" => ConfigScope.Project,
        "session" => ConfigScope.Session,
        _ => throw new ArgumentException($"Unknown configuration scope '{value}'. Valid scopes: user, project, session."),
    };

    private static string BuildApplyMessage(
        string action,
        ConfigScope scope,
        SettingDescriptor descriptor,
        ConfigApplyResult result)
    {
        var suffix = result.OverriddenChanges.Contains(descriptor.Key, StringComparer.OrdinalIgnoreCase)
            ? "; effective value is still overridden by a higher-precedence scope"
            : string.Empty;
        return $"{action} {descriptor.Key} in {scope.ToString().ToLowerInvariant()} scope ({ToDisplayName(descriptor.Activation)}{suffix})";
    }

    private static string ToDisplayName(ActivationMode mode) => mode switch
    {
        ActivationMode.Immediate => "immediate",
        ActivationMode.NextOperation => "next operation",
        ActivationMode.RestartRequired => "restart required",
        _ => mode.ToString(),
    };

    private static string FormatValue(object? value) => value switch
    {
        null => "(not set)",
        IEnumerable<string> strings => string.Join(", ", strings),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "(not set)",
    };
}
