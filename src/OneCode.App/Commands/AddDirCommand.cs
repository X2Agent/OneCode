using OneCode.Infrastructure.Config;
using System.Text;

namespace OneCode.App.Commands;

/// <summary>
/// /add-dir 命令——添加额外工作目录。
///
/// 两种模式：
/// - <c>/add-dir &lt;path&gt;</c>：写入 Session 作用域（内存），仅当前进程生效，不持久化。
/// - <c>/add-dir &lt;path&gt; --persist</c>：写入项目级 <c>.onecode/settings.json</c>，仅对此项目生效。
///
/// 两种模式统一经 <c>ApplyAsync</c> 生效——<c>Effective.AllowedDirectories</c> 的 getter
/// 每次返回新副本（<c>AppSettings.GetStringList</c>），直接 mutate 打不到真实状态上。
/// </summary>
public sealed class AddDirCommand(IConfigManager config) : Command
{
    public override string Name => "add-dir";
    public override string Description => "Add a directory to the project context";
    public override CommandCategory Category => CommandCategory.Builtin;
    public override string? ArgumentHint => "<path> [--persist]";

    public override async Task<CommandResult> ExecuteAsync(string[] args, CancellationToken ct = default)
    {
        if (args.Length == 0)
        {
            var dirs = config.Current.Effective.AllowedDirectories;
            if (dirs.Count == 0) return CommandResult.Text("No additional directories added.");
            var sb = new StringBuilder("Additional directories:\n");
            foreach (var d in dirs) sb.AppendLine(CultureInfo.InvariantCulture, $"  {d}");
            return CommandResult.Text(sb.ToString().TrimEnd());
        }

        var path = Path.GetFullPath(args[0]);
        if (!Directory.Exists(path))
            return CommandResult.Error($"Directory not found: {path}");

        var current = config.Current.Effective.AllowedDirectories;
        var merged = current.Contains(path) ? current : [.. current, path];
        var persist = args.Contains("--persist");

        // 会话级走 Session 作用域（内存层，不落盘）；--persist 写项目级 settings.json。
        var scope = persist ? ConfigScope.Project : ConfigScope.Session;
        var result = await config.ApplyAsync(
            ConfigPatch.Set(scope, "allowedDirectories", merged.ToArray()),
            ct).ConfigureAwait(false);
        if (!result.Saved)
            return CommandResult.Error(result.Error ?? "Failed to add the additional directory.");

        return CommandResult.Text(persist
            ? $"Added directory (persisted to project config): {path}"
            : $"Added directory (session only): {path}");
    }
}
