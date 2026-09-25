using System.Text;
using OneCode.App.Services.Hooks;
using OneCode.Infrastructure.Config;
namespace OneCode.App.Commands;

/// <summary>
/// /hooks 命令——展示已注册 hook、事件清单和策略状态
/// </summary>
public sealed class HooksCommand : Command
{
    private readonly HookRegistry _hookRegistry;
    private readonly HookPolicyService _policyService;
    private readonly HookLoadDiagnostics _loadDiagnostics;

    public HooksCommand(
        HookRegistry hookRegistry,
        HookPolicyService policyService,
        HookLoadDiagnostics loadDiagnostics)
    {
        ArgumentNullException.ThrowIfNull(hookRegistry);
        _hookRegistry = hookRegistry;
        ArgumentNullException.ThrowIfNull(policyService);
        _policyService = policyService;
        ArgumentNullException.ThrowIfNull(loadDiagnostics);
        _loadDiagnostics = loadDiagnostics;
    }

    public override string Name => "hooks";
    public override string Description => "View registered hooks, policy status, and lifecycle events";
    public override CommandCategory Category => CommandCategory.Builtin;
    public override string? ArgumentHint => "[list|events|status]";

    public override Task<CommandResult> ExecuteAsync(string[] args, CancellationToken ct = default)
    {
        if (args.Length == 0)
            return Task.FromResult(CommandResult.Text(BuildOverview()));

        return args[0].ToLowerInvariant() switch
        {
            "list" or "ls" => Task.FromResult(CommandResult.Text(BuildFullList())),
            "events" => Task.FromResult(CommandResult.Text(BuildEventList())),
            "status" => Task.FromResult(CommandResult.Text(BuildPolicyStatus())),
            _ => Task.FromResult(CommandResult.Error($"Unknown hooks subcommand: {args[0]}. Use: list, events, status")),
        };
    }

    private string BuildOverview()
    {
        var sb = new StringBuilder();
        sb.AppendLine("Hooks: agent interception points");
        sb.AppendLine();

        var persistent = _hookRegistry.GetAll();

        sb.AppendLine(CultureInfo.InvariantCulture, $"  Persistent hooks: {persistent.Count}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"  Config generation: {_hookRegistry.Generation}");
        sb.AppendLine();

        var trusted = _policyService.IsCurrentWorkspaceTrusted();
        sb.AppendLine(CultureInfo.InvariantCulture, $"  Workspace trusted: {(trusted ? "yes" : "NO")}");
        sb.AppendLine();
        sb.AppendLine("Config files:");
        sb.AppendLine("  ~/.onecode/hooks.json         (user-level, priority 100)");
        sb.AppendLine("  .onecode/hooks.json           (project-level, priority 200)");
        sb.AppendLine();

        var diagnostics = _loadDiagnostics.Reports;
        if (diagnostics.Count > 0)
        {
            sb.AppendLine("Load diagnostics (last bootstrap):");
            foreach (var report in diagnostics)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"  {report.ConfigDir}: {report.Status} ({report.HookCount} hooks)");
                foreach (var error in report.Errors)
                    sb.AppendLine(CultureInfo.InvariantCulture, $"    ! {error}");
            }
            sb.AppendLine();
        }

        sb.AppendLine("Subcommands: /hooks list | /hooks events | /hooks status");
        return sb.ToString();
    }

    private string BuildFullList()
    {
        var sb = new StringBuilder();
        var persistent = _hookRegistry.GetAll();

        if (persistent.Count == 0)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"No persistent hooks registered. Edit ~/{Constants.App.ConfigDirName}/hooks.json to add hooks.");
        }
        else
        {
            // Group by source (priority ranges: 0-99 managed, 100-199 user, 200-299 project)
            var groups = persistent.GroupBy(h => ClassifySource(h.Priority)).OrderBy(g => g.Key);
            foreach (var sourceGroup in groups)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"{sourceGroup.Key} hooks:");
                foreach (var pointGroup in sourceGroup.GroupBy(h => HookInterceptionPoints.ToWireName(h.Point)))
                {
                    sb.AppendLine(CultureInfo.InvariantCulture, $"  {pointGroup.Key}:");
                    foreach (var h in pointGroup.OrderBy(x => x.Priority))
                    {
                        sb.AppendLine(CultureInfo.InvariantCulture,
                            $"   [{h.Priority,3}] {h.ExecutorType,-10} {h.Name}");
                        if (h.Config is { } cfg)
                        {
                            if (!string.IsNullOrEmpty(cfg.Command))
                                sb.AppendLine(CultureInfo.InvariantCulture, $"             command: {Truncate(cfg.Command, 80)}");
                        }
                    }
                }
                sb.AppendLine();
            }
        }

        return sb.ToString();
    }

    private static string BuildEventList()
    {
        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"Available hook interception points ({HookPointMetadataRegistry.All.Count}):");
        sb.AppendLine();
        foreach (var meta in HookPointMetadataRegistry.All.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase))
        {
            sb.Append(CultureInfo.InvariantCulture, $"  {meta.Name,-25} {meta.Description}");
            if (meta.Matcher is { } mm)
                sb.Append(CultureInfo.InvariantCulture, $"  (matcher: {mm.PayloadField})");
            sb.AppendLine();
        }
        return sb.ToString();
    }

    private string BuildPolicyStatus()
    {
        var sb = new StringBuilder();
        sb.AppendLine("Hook Policy Status:");
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"  Workspace trusted:    {(_policyService.IsCurrentWorkspaceTrusted() ? "yes" : "NO — hooks will NOT fire")}");
        sb.AppendLine();
        sb.AppendLine("Hooks are configured in hooks.json files.");
        return sb.ToString();
    }

    private static string ClassifySource(int priority) => priority switch
    {
        < 100 => "Managed",
        < 200 => "User",
        < 300 => "Project",
        _ => "Plugin",
    };

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "...";
}
