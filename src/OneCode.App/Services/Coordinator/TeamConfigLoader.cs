using OneCode.App.Services.Agent;
using OneCode.Core.Coordinator;
using OneCode.Infrastructure;
using OneCode.Infrastructure.Config;

namespace OneCode.App.Services.Coordinator;

/// <summary>
/// Config loader extracted from TeamOrchestrationService.
///
/// 职责：团队配置的发现与解析，以及从 <see cref="AgentTemplateConfig"/> 构建 <see cref="TeamConfig"/>。
///
/// 统一 YAML 格式，移除 JSON 加载路径（team.json）。
/// 所有团队配置统一使用 team.yaml，与内置模板格式一致。
/// </summary>
internal static class TeamConfigLoader
{
    public static string GetTeamsDirectory()
    {
        var home = PathsHelper.UserHome;
        return Path.Combine(home, Constants.App.ConfigDirName, "teams");
    }

    /// <summary>查找团队配置文件路径（team.yaml）。</summary>
    public static string? GetTeamFilePath(string teamName)
    {
        var teamsDir = GetTeamsDirectory();
        var path = Path.Combine(teamsDir, teamName, "team.yaml");
        return File.Exists(path) ? path : null;
    }

    /// <summary>从 AgentTemplateConfig 构建 TeamConfig（YAML/内置模板共享逻辑）。</summary>
    public static TeamConfig BuildTeamConfigFromTemplate(AgentTemplateConfig template, string teamName)
    {
        var mode = string.Equals(template.Template, "magentic-orchestrator", StringComparison.OrdinalIgnoreCase)
            ? TeamOrchestrationMode.Magentic
            : TeamOrchestrationMode.GroupChat;

        List<TeamMember> members = [];
        foreach (var w in template.Workers)
        {
            // AgentId 优先用 YAML 中的 name 字段（如 "researcher-a"/"researcher-b"），
            // 避免 research.yaml 中两个 role:researcher 的成员得到相同 AgentId。
            // 若 name 为空则用 role 拼接，并在重复时追加索引后缀。
            var baseName = !string.IsNullOrWhiteSpace(w.Name) ? w.Name! : w.Role ?? "member";
            var agentId = $"{teamName}-{baseName}";
            // 去重：若 AgentId 已出现（不同 worker 同名），追加 -2/-3 后缀
            if (members.Any(m => string.Equals(m.AgentId, agentId, StringComparison.OrdinalIgnoreCase)))
            {
                var suffix = 2;
                while (members.Any(m => string.Equals(m.AgentId, $"{agentId}-{suffix}", StringComparison.OrdinalIgnoreCase)))
                    suffix++;
                agentId = $"{agentId}-{suffix}";
            }
            members.Add(new TeamMember(agentId, w.Role, w.Instructions, w.AllowedTools));
        }

        // Magentic 模式若无 lead/orchestrator，自动插入
        if (mode == TeamOrchestrationMode.Magentic &&
            !members.Any(m => m.Role is "lead" or "orchestrator"))
        {
            members.Insert(0, new TeamMember($"{teamName}-orchestrator", "orchestrator", template.Instructions));
        }

        if (members.Count == 0)
            members.Add(new TeamMember($"{teamName}-lead", "lead", template.Instructions));

        return new TeamConfig(teamName, "(builtin)", members, template.MaxRounds, mode);
    }

    /// <summary>从 YAML 文件加载团队配置。</summary>
    public static TeamConfig LoadTeamFromYaml(string yamlPath, string teamName)
    {
        var template = AgentTemplateConfig.FromYamlFile(yamlPath);
        return BuildTeamConfigFromTemplate(template, teamName) with { FilePath = yamlPath };
    }

    /// <summary>
    /// 扫描用户团队目录（~/.onecode/teams/），加载所有 team.yaml。
    /// 在应用启动时调用，使 /team list 能显示用户自定义团队。
    /// </summary>
    public static IReadOnlyList<(string Name, string FilePath)> DiscoverUserTeams()
    {
        var teamsDir = GetTeamsDirectory();
        var result = new List<(string, string)>();

        if (!Directory.Exists(teamsDir))
            return result;

        foreach (var dir in Directory.GetDirectories(teamsDir))
        {
            var yamlPath = Path.Combine(dir, "team.yaml");
            if (!File.Exists(yamlPath)) continue;

            var name = Path.GetFileName(dir);
            result.Add((name, yamlPath));
        }

        return result;
    }
}
