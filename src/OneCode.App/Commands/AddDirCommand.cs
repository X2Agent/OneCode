using OneCode.Infrastructure.Config;
using System.Text;

namespace OneCode.App.Commands;

/// <summary>
/// /add-dir 命令——添加额外工作目录。
///
/// 两种模式：
/// - <c>/add-dir &lt;path&gt;</c>：仅当前会话生效（内存），不持久化。
/// - <c>/add-dir &lt;path&gt; --persist</c>：写入项目级 <c>.onecode/settings.json</c>，仅对此项目生效。
///
/// <c>--persist</c> 写入项目级配置文件，彻底隔离不同项目的额外目录。
/// </summary>
public sealed class AddDirCommand(IConfigManager config) : Command
{
    public override string Name => "add-dir";
    public override string Description => "Add a directory to the project context";
    public override CommandCategory Category => CommandCategory.Builtin;
    public override string? ArgumentHint => "<path> [--persist]";

    private List<string> AllowedDirs => config.Current.Effective.AllowedDirectories;

    public override async Task<CommandResult> ExecuteAsync(string[] args, CancellationToken ct = default)
    {
        if (args.Length == 0)
        {
            if (AllowedDirs.Count == 0) return CommandResult.Text("No additional directories added.");
            var sb = new StringBuilder("Additional directories:\n");
            foreach (var d in AllowedDirs) sb.AppendLine(CultureInfo.InvariantCulture, $"  {d}");
            return CommandResult.Text(sb.ToString().TrimEnd());
        }

        var path = Path.GetFullPath(args[0]);
        if (!Directory.Exists(path))
            return CommandResult.Error($"Directory not found: {path}");

        if (!AllowedDirs.Contains(path))
            AllowedDirs.Add(path);

        if (args.Contains("--persist"))
        {
            // 写入项目级 .onecode/settings.json，而非全局 ~/.onecode/settings.json。
            // 项目级 allowedDirectories 会覆盖全局同名键，确保项目间隔离。
            var dirsArray = AllowedDirs.ToArray();
            var result = await config.ApplyAsync(
                ConfigPatch.Set(ConfigScope.Project, "allowedDirectories", dirsArray),
                ct).ConfigureAwait(false);
            if (!result.Saved)
                return CommandResult.Error(result.Error ?? "Failed to persist the additional directory.");
            return CommandResult.Text($"Added directory (persisted to project config): {path}");
        }

        return CommandResult.Text($"Added directory (session only): {path}");
    }
}
