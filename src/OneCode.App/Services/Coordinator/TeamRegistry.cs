using OneCode.App.Services.Agent;

namespace OneCode.App.Services.Coordinator;

/// <summary>
/// 团队注册表 — 从 <see cref="TeamOrchestrationService"/> 收敛出的注册/发现职责：
/// 内存注册表（ConcurrentDictionary）、内置团队嵌入式资源加载、用户团队目录扫描、
/// 活跃团队状态，以及配置 advisory 透出。不含任何工作流执行逻辑。
/// </summary>
internal sealed class TeamRegistry(ILogger logger)
{
    private readonly ConcurrentDictionary<string, TeamConfig> _teams = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>当前活跃团队。为 null 时回退到第一个注册的团队。</summary>
    public string? ActiveTeam { get; set; }

    public IReadOnlyList<string> RegisteredTeams =>
        _teams.Keys.OrderBy(k => k).ToList();

    public bool Contains(string teamName) => _teams.ContainsKey(teamName);

    public bool TryGet(string teamName, out TeamConfig config) =>
        _teams.TryGetValue(teamName, out config!);

    public void Put(string teamName, TeamConfig config) => _teams[teamName] = config;

    public bool Remove(string teamName) => _teams.TryRemove(teamName, out _);

    /// <summary>获取当前应使用的团队名（ActiveTeam 或第一个注册的团队）。</summary>
    public string? ResolveActiveTeam()
    {
        if (!string.IsNullOrEmpty(ActiveTeam) && _teams.ContainsKey(ActiveTeam))
            return ActiveTeam;
        var teams = RegisteredTeams;
        return teams.Count > 0 ? teams[0] : null;
    }

    // 内置团队模板：从嵌入式资源加载（OneCode.App.prompts.teams.{name}.yaml）
    public static readonly string[] BuiltinTeamTemplates = ["code-review", "research", "impl"];

    /// <summary>
    /// 注册内置团队模板 + 扫描用户团队目录。
    /// 幂等：已注册的同名团队不会被覆盖；默认活跃团队取首个内置团队。
    /// </summary>
    public Task RegisterBuiltinAndUserTeamsAsync(CancellationToken ct = default)
    {
        var assembly = typeof(TeamOrchestrationService).Assembly;

        foreach (var name in BuiltinTeamTemplates)
        {
            if (_teams.ContainsKey(name))
                continue;

            var resourceName = $"OneCode.App.prompts.teams.{name}.yaml";
            try
            {
                using var stream = assembly.GetManifestResourceStream(resourceName);
                if (stream is null)
                {
                    logger.LogWarning("Built-in team template resource not found: {Resource}", resourceName);
                    continue;
                }

                using var reader = new StreamReader(stream);
                var yaml = reader.ReadToEnd();
                RegisterFromTemplate(AgentTemplateConfig.FromYaml(yaml), name, source: "Built-in");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to load built-in team template: {Resource}", resourceName);
            }
        }

        // 扫描用户团队目录（~/.onecode/teams/*/team.yaml）
        // 使 /team list 能显示用户自定义团队，无需先触发一次查询才注册。
        foreach (var (name, filePath) in TeamConfigLoader.DiscoverUserTeams())
        {
            if (_teams.ContainsKey(name))
                continue;

            try
            {
                RegisterFromFile(filePath, name);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to load user team '{Name}' from {Path}", name, filePath);
            }
        }

        // 默认活跃团队：取首个内置团队（RegisteredTeams 已按名称排序，确定性可预期）。
        if (string.IsNullOrEmpty(ActiveTeam))
            ActiveTeam = RegisteredTeams.FirstOrDefault();

        return Task.CompletedTask;
    }

    /// <summary>从 YAML 文件注册团队，并透出配置 advisory。</summary>
    public void RegisterFromFile(string yamlFilePath, string teamName)
    {
        var template = AgentTemplateConfig.FromYamlFile(yamlFilePath);
        foreach (var advisory in TeamConfigLoader.BuildAdvisories(template, teamName))
            logger.LogWarning("{Advisory}", advisory);

        var config = TeamConfigLoader.BuildTeamConfigFromTemplate(template, teamName) with { FilePath = yamlFilePath };
        Put(teamName, config);
        logger.LogInformation(
            "Team '{TeamName}' registered: mode={Mode} members={Count}",
            teamName, config.Mode, config.Members.Count);
    }

    /// <summary>从已解析模板注册团队（内置资源路径共用），并透出配置 advisory。</summary>
    public void RegisterFromTemplate(AgentTemplateConfig template, string teamName, string source)
    {
        foreach (var advisory in TeamConfigLoader.BuildAdvisories(template, teamName))
            logger.LogWarning("{Advisory}", advisory);

        var config = TeamConfigLoader.BuildTeamConfigFromTemplate(template, teamName);
        Put(teamName, config);
        logger.LogInformation(
            "{Source} team '{Name}' registered: mode={Mode} members={Count}",
            source, teamName, config.Mode, config.Members.Count);
    }
}
