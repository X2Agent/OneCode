using System.Text;
using OneCode.Core.Coordinator;

namespace OneCode.App.Commands;

/// <summary>
/// /team — 团队管理命令。
///
/// 子命令：
///   /team                  → 显示当前团队 + 已注册团队列表
///   /team list             → 同上
///   /team &lt;name&gt;            → 切换活跃团队
///   /team switch &lt;name&gt;      → 同上（显式 switch）
///   /team info [&lt;name&gt;]     → 显示团队详情（成员、模式、轮数）
///
/// 内置团队：feature-impl / code-review / research
/// 用户自定义团队：~/.onecode/teams/{name}/team.yaml
/// </summary>
public sealed class TeamCommand(ITeamOrchestrationService teamService) : Command
{
    private static readonly HashSet<string> BuiltinTeams = new(StringComparer.OrdinalIgnoreCase)
    {
        "feature-impl", "code-review", "research"
    };

    public override string Name => "team";
    public override string Description => "Manage agent teams: list, switch active team, or show details";
    public override CommandCategory Category => CommandCategory.Builtin;
    public override string? ArgumentHint => "[list|switch <name>|info [<name>]|<name>]";
    public override IReadOnlyList<string> Aliases => ["teams"];

    public override Task<CommandResult> ExecuteAsync(string[] args, CancellationToken ct = default)
    {
        var sub = args.Length > 0 ? args[0].ToLowerInvariant() : "list";

        // /team <name>  → 直接切换
        if (sub is not ("list" or "switch" or "info" or "help") && args.Length == 1)
            return Task.FromResult(SwitchTeam(args[0]));

        return Task.FromResult(sub switch
        {
            "list" or "ls" => ListTeams(),
            "switch" or "use" => args.Length > 1
                ? SwitchTeam(args[1])
                : CommandResult.Error("Usage: /team switch <name>"),
            "info" or "show" => args.Length > 1
                ? ShowTeamInfo(args[1])
                : ShowTeamInfo(teamService.ResolveActiveTeam()),
            "help" => ShowHelp(),
            _ => CommandResult.Error(
                $"Unknown subcommand: {sub}. Use: list, switch <name>, info [<name>], or /team <name>"),
        });
    }

    private CommandResult ListTeams()
    {
        var teams = teamService.RegisteredTeams;
        var active = teamService.ResolveActiveTeam();

        if (teams.Count == 0)
            return CommandResult.Text("No teams registered. Built-in teams (feature-impl, code-review, research) should auto-register on startup.");

        var sb = new StringBuilder("Teams:");
        foreach (var name in teams)
        {
            var marker = string.Equals(name, active, StringComparison.OrdinalIgnoreCase) ? " *" : "";
            var tag = BuiltinTeams.Contains(name) ? " (built-in)" : " (user)";
            var mode = teamService.GetTeamMode(name) ?? "?";
            sb.AppendLine(CultureInfo.InvariantCulture, $"  {name}{marker}{tag}  [{mode}]");
        }
        sb.AppendLine();
        sb.AppendLine("* = active  |  Use /team <name> to switch");

        return CommandResult.Text(sb.ToString().TrimEnd());
    }

    private CommandResult SwitchTeam(string name)
    {
        var teams = teamService.RegisteredTeams;
        if (!teams.Contains(name))
        {
            var available = string.Join(", ", teams);
            return CommandResult.Error(
                $"Team '{name}' not registered. Available: {available}");
        }

        teamService.ActiveTeam = name;
        var mode = teamService.GetTeamMode(name);
        return CommandResult.Text($"Active team switched to: {name} [{mode}]");
    }

    private CommandResult ShowTeamInfo(string? name)
    {
        name ??= teamService.ResolveActiveTeam();
        if (string.IsNullOrEmpty(name))
            return CommandResult.Error("No active team. Use /team <name> to switch.");

        var teams = teamService.RegisteredTeams;
        if (!teams.Contains(name))
            return CommandResult.Error($"Team '{name}' not registered.");

        var mode = teamService.GetTeamMode(name) ?? "unknown";
        var tag = BuiltinTeams.Contains(name) ? "built-in" : "user-defined";

        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"Team: {name} ({tag})");
        sb.AppendLine(CultureInfo.InvariantCulture, $"  Mode: {mode}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"  Active: {(string.Equals(name, teamService.ResolveActiveTeam(), StringComparison.OrdinalIgnoreCase) ? "yes" : "no")}");

        // 成员列表：显示每个成员的角色和是否为协调者
        var members = teamService.GetTeamMembers(name);
        if (members is { Count: > 0 })
        {
            sb.AppendLine("  Members:");
            foreach (var m in members)
            {
                var roleTag = m.IsOrchestrator ? " (orchestrator)" : "";
                var role = string.IsNullOrEmpty(m.Role) ? m.AgentId : m.Role;
                sb.AppendLine(CultureInfo.InvariantCulture, $"    - {role}{roleTag} [{m.AgentId}]");
            }
        }

        return CommandResult.Text(sb.ToString().TrimEnd());
    }

    private CommandResult ShowHelp()
    {
        return CommandResult.Text("""
            Team command — manage agent teams.

            Usage:
              /team                  List all registered teams
              /team <name>           Switch active team
              /team switch <name>    Same as above
              /team info [<name>]    Show team details (defaults to active)

            Built-in teams:
              feature-impl   Magentic — orchestrator + researcher + executor + tester
              code-review    GroupChat — reviewer + architect + researcher
              research       GroupChat — planner + 2×researcher + architect

            User-defined teams:
              Place team.yaml at ~/.onecode/teams/{name}/team.yaml
              and register with /team <name> (auto-loaded on first use).
            """);
    }
}
