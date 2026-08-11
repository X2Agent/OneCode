using OneCode.Infrastructure.Config;

namespace OneCode.Infrastructure.Mcp;

public sealed class McpMultiScopeConfigLoader
{
    private const string ConfigFileName = ".mcp.json";
    private const string LocalConfigFileName = ".mcp.local.json";

    private readonly ILogger<McpMultiScopeConfigLoader> _logger;

    public McpMultiScopeConfigLoader(ILogger<McpMultiScopeConfigLoader> logger)
    {
        _logger = logger;
    }

    public static string GetUserConfigPath()
    {
        var home = PathsHelper.UserHome;
        return Path.Combine(home, Constants.App.ConfigDirName, ConfigFileName);
    }

    public static string? FindProjectConfigPath(string? workingDirectory = null)
    {
        var dir = workingDirectory ?? Environment.CurrentDirectory;
        dir = Path.GetFullPath(dir);

        while (!string.IsNullOrEmpty(dir))
        {
            var candidate = Path.Combine(dir, ConfigFileName);
            if (File.Exists(candidate))
                return candidate;

            if (Directory.Exists(Path.Combine(dir, ".git")))
                break;

            var parent = Path.GetDirectoryName(dir);
            if (parent == dir)
                break;
            dir = parent;
        }

        return null;
    }

    public string? FindLocalConfigPath(string? workingDirectory = null)
    {
        var projectPath = FindProjectConfigPath(workingDirectory);
        if (string.IsNullOrEmpty(projectPath))
            return null;

        var localPath = Path.Combine(Path.GetDirectoryName(projectPath)!, LocalConfigFileName);
        return File.Exists(localPath) ? localPath : null;
    }

    public async Task<MergedMcpConfig> LoadAllAsync(
        string? workingDirectory = null,
        CancellationToken ct = default)
    {
        var userPath = GetUserConfigPath();
        var projectPath = FindProjectConfigPath(workingDirectory);
        var localPath = FindLocalConfigPath(workingDirectory);

        ParsedMcpServers? userConfig = null;
        ParsedMcpServers? projectConfig = null;
        ParsedMcpServers? localConfig = null;

        if (File.Exists(userPath))
        {
            try
            {
                userConfig = await McpConfigParser.ParseFromFileAsync(userPath, ct).ConfigureAwait(false);
                _logger.LogDebug("Loaded user MCP config from {Path}", userPath);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load user MCP config from {Path}", userPath);
            }
        }

        if (!string.IsNullOrEmpty(projectPath))
        {
            try
            {
                projectConfig = await McpConfigParser.ParseFromFileAsync(projectPath, ct).ConfigureAwait(false);
                _logger.LogDebug("Loaded project MCP config from {Path}", projectPath);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load project MCP config from {Path}", projectPath);
            }
        }

        if (!string.IsNullOrEmpty(localPath))
        {
            try
            {
                localConfig = await McpConfigParser.ParseFromFileAsync(localPath, ct).ConfigureAwait(false);
                _logger.LogDebug("Loaded local MCP config from {Path}", localPath);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load local MCP config from {Path}", localPath);
            }
        }

        var merged = MergeConfigs(userConfig, projectConfig, localConfig);

        return new MergedMcpConfig(
            Servers: merged.Servers,
            UserConfigPath: userPath,
            ProjectConfigPath: projectPath,
            LocalConfigPath: localPath);
    }

    private static ParsedMcpServers MergeConfigs(
        ParsedMcpServers? user,
        ParsedMcpServers? project,
        ParsedMcpServers? local)
    {
        var merged = new Dictionary<string, McpServerDefinition>(
            StringComparer.OrdinalIgnoreCase);

        if (user?.Servers != null)
        {
            foreach (var (name, def) in user.Servers)
                merged[name] = def;
        }

        if (project?.Servers != null)
        {
            foreach (var (name, def) in project.Servers)
                merged[name] = def;
        }

        if (local?.Servers != null)
        {
            foreach (var (name, def) in local.Servers)
                merged[name] = def;
        }

        return new ParsedMcpServers(merged);
    }
}

public sealed record MergedMcpConfig(
    IReadOnlyDictionary<string, McpServerDefinition> Servers,
    string UserConfigPath,
    string? ProjectConfigPath = null,
    string? LocalConfigPath = null);
