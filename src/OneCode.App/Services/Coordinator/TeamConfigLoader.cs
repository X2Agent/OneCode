using OneCode.App.Services.Agent;
using OneCode.Core.Coordinator;
using OneCode.Infrastructure;
using OneCode.Infrastructure.Config;

namespace OneCode.App.Services.Coordinator;

/// <summary>
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
        // 模式映射统一走 TeamOrchestrationModeExtensions.FromYamlTemplate（单一事实源）。
        var mode = TeamOrchestrationModeExtensions.FromYamlTemplate(template.Template);

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
            members.Add(new TeamMember(agentId, w.Role,
                mode == TeamOrchestrationMode.GroupChat ? WithCollaborationProtocol(w.Instructions) : w.Instructions,
                w.AllowedTools));
        }

        if (members.Count == 0)
            members.Add(new TeamMember($"{teamName}-lead", "lead", template.Instructions));

        return EnsureOrchestrator(
            new TeamConfig(teamName, "(builtin)", members, template.MaxRounds, mode),
            fallbackInstructions: template.Instructions);
    }

    /// <summary>
    /// GroupChat 成员协作协议：Round-Robin 轮询下最容易退化的问题是成员礼貌性附和、空转烧轮次。
    /// 在加载期把协议注入每个成员 instructions，要求发言必须推进讨论，无可贡献时显式 PASS，
    /// 配合 GroupChat 的提前收敛判定（预算过半后无实质发言即终止）降低无效轮次。
    /// </summary>
    internal const string CollaborationProtocol =
        """

        [GroupChat 协作协议]
        - 你的每次发言必须推进讨论：补充新证据、反驳具体观点、或提出替代方案。
        - 禁止单纯附和或复述他人结论；若本轮无可贡献内容，仅回复 "[PASS]"。
        - 引用其他成员的具体观点时指明其角色名。
        """;

    internal static string WithCollaborationProtocol(string? instructions)
        => string.IsNullOrWhiteSpace(instructions)
            ? CollaborationProtocol.TrimStart()
            : instructions.TrimEnd() + "\n" + CollaborationProtocol;

    /// <summary>
    /// 配置级 advisory：解析成功但存在值得提示的选型问题时返回非空列表。
    /// 由注册方（TeamOrchestrationService）写日志透出。
    /// </summary>
    public static IReadOnlyList<string> BuildAdvisories(AgentTemplateConfig template, string teamName)
    {
        List<string> advisories = [];
        var mode = TeamOrchestrationModeExtensions.FromYamlTemplate(template.Template);

        // 视角独立型团队误用 GroupChat：全共享上下文成本高且互相污染视角，
        // 这类"只从 X 视角审查"的团队更适合 parallel-dag（隔离 + 聚合去重）。
        if (mode == TeamOrchestrationMode.GroupChat && template.Workers.Count >= 3)
        {
            var perspectiveCount = template.Workers.Count(w =>
                (w.Instructions ?? "").Contains("视角", StringComparison.OrdinalIgnoreCase) ||
                (w.Instructions ?? "").Contains("不要评价", StringComparison.OrdinalIgnoreCase));
            if (perspectiveCount >= 2)
                advisories.Add(
                    $"Team '{teamName}': 检测到 {perspectiveCount} 个视角独立型成员使用 groupchat 模式。" +
                    "若各成员无需互相回应，建议改用 template: parallel-dag（上下文隔离，成本更低）。");
        }

        return advisories;
    }

    /// <summary>
    /// Magentic 模式若无 lead/orchestrator 成员则自动插入一个编排者。
    /// 模板加载与 TUI overrideMode 覆盖两条路径共用此方法，
    /// 保证任何切到 Magentic 的团队都有合格的编排者（P2-8 残缺团队修复）。
    /// 插入的成员 SystemPrompt 为空——TeamAgentFactory 按 role 解析 system/coordinator prompt。
    /// </summary>
    public static TeamConfig EnsureOrchestrator(TeamConfig config, string? fallbackInstructions = null)
    {
        if (config.Mode != TeamOrchestrationMode.Magentic ||
            config.Members.Any(m => m.Role is "lead" or "orchestrator"))
            return config;

        var members = config.Members.ToList();
        members.Insert(0, new TeamMember(
            $"{config.TeamName}-orchestrator", "orchestrator", fallbackInstructions));
        return config with { Members = members };
    }

    /// <summary>
    /// 扫描用户团队目录（~/.onecode/teams/），加载所有 team.yaml。
    /// 在应用启动时调用，使 /team list 能显示用户自定义团队。
    /// </summary>
    public static IReadOnlyList<(string Name, string FilePath)> DiscoverUserTeams()
    {
        var teamsDir = GetTeamsDirectory();
        List<(string, string)> result = [];

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
