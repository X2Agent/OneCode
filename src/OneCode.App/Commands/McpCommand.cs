using System.Text;
using System.Text.Json.Serialization;
using OneCode.Infrastructure.Mcp;

namespace OneCode.App.Commands;

/// <summary>JSON config-file entry for a single MCP server (distinct from the runtime McpServerDefinition).</summary>
internal sealed record McpConfigEntry(
    [property: JsonPropertyName("type")] string? Type = null,
    [property: JsonPropertyName("command")] string? Command = null,
    [property: JsonPropertyName("args")] string[]? Args = null,
    [property: JsonPropertyName("url")] string? Url = null,
    [property: JsonPropertyName("env")] Dictionary<string, string>? Env = null,
    [property: JsonPropertyName("disabled")] bool? Disabled = null);

/// <summary>Root object of MCP config files (user-scope .mcp.json, project-scope .mcp.json).</summary>
internal sealed class McpConfigFile
{
    [JsonPropertyName("mcpServers")]
    public Dictionary<string, McpConfigEntry> McpServers { get; set; } = new();
}

/// <summary>
/// Manages MCP servers: discover (search) and install from the Smithery registry,
/// add/remove/enable/disable local config, and connect/disconnect at runtime.
/// All runtime state flows through <see cref="IMcpConnectionManager"/> — the single
/// source of truth that also feeds the LLM tool catalog, so connect/disconnect
/// here immediately affects which tools the model can call.
/// </summary>
public sealed class McpCommand(
    IMcpConnectionManager connectionManager,
    McpRegistryClient registryClient,
    McpMultiScopeConfigLoader configLoader,
    ILogger<McpCommand> logger) : Command
{
    public override string Name => "mcp";
    public override string Description => "Manage MCP server connections";
    public override CommandCategory Category => CommandCategory.Skill;
    public override string? ArgumentHint => "[list|get|search|install|add|remove|connect|disconnect|enable|disable] <args>";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public override async Task<CommandResult> ExecuteAsync(string[] args, CancellationToken ct = default)
    {
        if (args.Length == 0) return CommandResult.Text(await ListServersAsync(ct));

        return args[0].ToLowerInvariant() switch
        {
            "list" or "ls" => CommandResult.Text(await ListServersAsync(ct)),
            "get" when args.Length > 1 => CommandResult.Text(await GetServerAsync(args[1], ct)),
            "search" when args.Length > 1 => CommandResult.Text(await SearchAsync(args[1..], ct)),
            "install" when args.Length > 1 => CommandResult.Text(await InstallAsync(args[1..], ct)),
            "add" => CommandResult.Text(await AddServerAsync(args[1..], ct)),
            "remove" or "rm" => CommandResult.Text(await RemoveServerAsync(args[1..], ct)),
            "connect" => CommandResult.Text(await ConnectAsync(args[1..], ct)),
            "disconnect" => CommandResult.Text(await DisconnectAsync(args[1..], ct)),
            "enable" => CommandResult.Text(await ToggleEnabledAsync(args[1..], true, ct)),
            "disable" => CommandResult.Text(await ToggleEnabledAsync(args[1..], false, ct)),
            _ => CommandResult.Error($"Unknown MCP command: {args[0]}"),
        };
    }

    private async Task<string> ListServersAsync(CancellationToken ct)
    {
        var merged = await configLoader.LoadAllAsync(ct: ct).ConfigureAwait(false);
        var configured = merged.Servers;

        // Runtime status snapshot — name → (connected, toolCount)
        var status = connectionManager.GetStatus()
            .ToDictionary(s => s.Name, StringComparer.OrdinalIgnoreCase);

        var sb = new StringBuilder("MCP Servers:\n");

        if (configured.Count > 0)
        {
            foreach (var (name, def) in configured.OrderBy(c => c.Key, StringComparer.OrdinalIgnoreCase))
            {
                var connected = status.TryGetValue(name, out var st) && st.IsConnected;
                var tools = connected ? $"{st!.ToolCount} tools" : "";
                var enabledTag = def.Disabled ? "[disabled]   " : "[enabled]    ";
                var connTag = connected ? "[connected]    " : "[disconnected] ";
                sb.AppendLine(CultureInfo.InvariantCulture, $"  {name,-24} {enabledTag}{connTag}{tools}");
            }
        }
        else
        {
            sb.AppendLine("  No servers configured.");
        }

        sb.AppendLine();
        sb.AppendLine("Use '/mcp search <query>' to discover servers in the registry.");
        sb.AppendLine("Use '/mcp install <name>' to install from the registry.");
        sb.AppendLine("Use '/mcp connect <name>' to connect a configured server.");
        return sb.ToString().TrimEnd();
    }

    private async Task<string> GetServerAsync(string name, CancellationToken ct)
    {
        var merged = await configLoader.LoadAllAsync(ct: ct).ConfigureAwait(false);
        if (!merged.Servers.TryGetValue(name, out var def))
            return $"MCP server '{name}' not found in configuration. Use /mcp list to see all servers.";

        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"## {name}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Transport:  {def.TransportType}");
        if (def.Command is not null)
            sb.AppendLine($"Command:    {def.Command} {string.Join(" ", def.Args ?? [])}".TrimEnd());
        if (def.Url is not null)
            sb.AppendLine(CultureInfo.InvariantCulture, $"URL:        {def.Url}");
        if (def.Env is { Count: > 0 })
        {
            sb.AppendLine("Environment:");
            foreach (var (k, _) in def.Env)
                sb.AppendLine(CultureInfo.InvariantCulture, $"  {k} = ***");  // don't leak secrets
        }
        sb.AppendLine(CultureInfo.InvariantCulture, $"Enabled:    {(!def.Disabled ? "yes" : "no")}");

        var client = connectionManager.GetClient(name);
        if (client?.IsConnected == true)
        {
            try
            {
                var tools = await client.ListToolsAsync(ct).ConfigureAwait(false);
                sb.AppendLine(CultureInfo.InvariantCulture, $"\nTools ({tools.Count}):");
                foreach (var tool in tools.OrderBy(t => t.Name))
                    sb.AppendLine(CultureInfo.InvariantCulture, $"  {tool.Name,-30} {tool.Description}");
            }
            catch (Exception ex)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"\nCould not list tools: {ex.Message}");
            }
        }
        else
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"\nNot connected. Use /mcp connect {name} to connect.");
        }

        return sb.ToString().TrimEnd();
    }

    // search

    private async Task<string> SearchAsync(string[] args, CancellationToken ct)
    {
        var query = string.Join(' ', args);
        if (string.IsNullOrWhiteSpace(query))
            return "Usage: /mcp search <query>";

        var results = await registryClient.SearchAsync(query, limit: 20, ct).ConfigureAwait(false);
        if (results.Count == 0)
            return $"No MCP servers found for '{query}'.";

        var sb = new StringBuilder($"Registry search: '{query}' ({results.Count} results)\n\n");
        foreach (var s in results)
        {
            var badge = s.Verified ? " ✓" : "";
            var host = s.Remote ? "remote" : "local";
            sb.AppendLine(CultureInfo.InvariantCulture, $"  {s.QualifiedName,-32}{badge} ({host}) ↓{s.UseCount}");
            if (!string.IsNullOrEmpty(s.DisplayName) && s.DisplayName != s.QualifiedName)
                sb.AppendLine(CultureInfo.InvariantCulture, $"    {s.DisplayName}");
            if (!string.IsNullOrWhiteSpace(s.Description))
            {
                var desc = s.Description.Length > 90 ? s.Description[..90] + "…" : s.Description;
                sb.AppendLine(CultureInfo.InvariantCulture, $"    {desc}");
            }
        }
        sb.AppendLine();
        sb.AppendLine("Use '/mcp install <qualifiedName>' to install a server.");
        return sb.ToString().TrimEnd();
    }

    // install

    private async Task<string> InstallAsync(string[] args, CancellationToken ct)
    {
        // /mcp install <qualifiedName> [--name <name>] [--scope project|user] [--connect]
        var qualifiedName = args[0];
        string? customName = null;
        var scope = "project";
        var connect = false;
        for (var i = 1; i < args.Length; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--name": if (++i < args.Length) customName = args[i]; break;
                case "--scope": if (++i < args.Length) scope = args[i]; break;
                case "--connect": connect = true; break;
            }
        }

        var server = await registryClient.GetServerAsync(qualifiedName, ct).ConfigureAwait(false);
        if (server is null)
            return $"MCP server '{qualifiedName}' not found in the registry.";

        var conn = server.Connections?.FirstOrDefault(c => !string.IsNullOrEmpty(c.DeploymentUrl));
        if (conn is null || string.IsNullOrEmpty(conn.DeploymentUrl))
            return $"'{qualifiedName}' has no installable connection info (it may be a local-only server). Use '/mcp add' to configure manually.";

        var name = customName ?? qualifiedName.Replace('/', '-');
        var entry = new McpConfigEntry
        {
            Type = conn.Type ?? "http",
            Url = conn.DeploymentUrl,
        };

        var configPath = GetConfigPath(scope);
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        var file = LoadConfigFile(configPath) ?? new McpConfigFile();
        file.McpServers[name] = entry;
        await SaveConfigFileAsync(configPath, file, ct).ConfigureAwait(false);

        logger.LogInformation("Installed MCP server '{Name}' from registry", name);

        var msg = $"Installed '{name}' ({server.DisplayName}) → {scope} config ({conn.Type}: {conn.DeploymentUrl}).";

        // Warn if the server declares config parameters the user may need to fill in.
        if (conn.ConfigSchema is { ValueKind: JsonValueKind.Object } schema
            && schema.TryGetProperty("properties", out var props)
            && props.EnumerateObject().MoveNext())
        {
            msg += " Note: this server declares config parameters — edit the config file if tools require authentication.";
        }

        if (connect)
        {
            var ok = await connectionManager.ConnectOneAsync(name, ct).ConfigureAwait(false);
            msg += ok ? " Connected." : $" Connection failed — use '/mcp connect {name}'.";
        }
        else
        {
            msg += $" Use '/mcp connect {name}' to connect.";
        }

        return msg;
    }

    private async Task<string> AddServerAsync(string[] args, CancellationToken ct)
    {
        if (args.Length < 1)
            return "Usage: /mcp add <name> --transport stdio|sse|http|ws [--command <cmd>] [--url <url>] [--args ...] [--scope project|user] [--connect]";
        var name = args[0];
        string? type = null, command = null, url = null, scope = "project";
        var connect = false;
        List<string> cmdArgs = [];
        for (var i = 1; i < args.Length; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--type":
                case "--transport": if (++i < args.Length) type = args[i]; break;
                case "--command": if (++i < args.Length) command = args[i]; break;
                case "--url": if (++i < args.Length) url = args[i]; break;
                case "--scope": if (++i < args.Length) scope = args[i]; break;
                case "--connect": connect = true; break;
                case "--args": while (++i < args.Length && !args[i].StartsWith("--", StringComparison.Ordinal)) cmdArgs.Add(args[i]); i--; break;
            }
        }

        if (type is null) return "Missing --transport. Use stdio, sse, http, or ws.";
        var normalized = type.ToLowerInvariant() switch
        {
            "ws" or "websocket" => "ws",
            "sse" or "sse-ide" => "sse",
            "http" or "streamable-http" => "http",
            "stdio" or "std" => "stdio",
            _ => type.ToLowerInvariant()
        };

        var entry = new McpConfigEntry
        {
            Type = normalized,
            Command = command,
            Args = cmdArgs.Count > 0 ? cmdArgs.ToArray() : null,
            Url = url,
        };

        var configPath = GetConfigPath(scope);
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        var file = LoadConfigFile(configPath) ?? new McpConfigFile();
        file.McpServers[name] = entry;
        await SaveConfigFileAsync(configPath, file, ct).ConfigureAwait(false);
        logger.LogInformation("Added MCP server: {Name}", name);

        var msg = $"MCP server '{name}' added to {scope} config (transport: {normalized}).";
        if (connect)
        {
            var ok = await connectionManager.ConnectOneAsync(name, ct).ConfigureAwait(false);
            msg += ok ? " Connected." : $" Use '/mcp connect {name}' to connect.";
        }
        else
        {
            msg += $" Use '/mcp connect {name}' to connect.";
        }
        return msg;
    }

    private async Task<string> RemoveServerAsync(string[] args, CancellationToken ct)
    {
        if (args.Length < 1) return "Usage: /mcp remove <name>";
        var name = args[0];

        try { await connectionManager.DisconnectAsync(name).ConfigureAwait(false); }
        catch (Exception ex) { logger.LogWarning(ex, "Error disconnecting '{Name}' during removal", name); }

        foreach (var scope in new[] { "project", "user" })
        {
            var path = GetConfigPath(scope);
            if (!File.Exists(path)) continue;
            var file = LoadConfigFile(path);
            if (file?.McpServers.Remove(name) == true)
            {
                await SaveConfigFileAsync(path, file, ct).ConfigureAwait(false);
                return $"MCP server '{name}' removed.";
            }
        }
        return $"MCP server '{name}' not found.";
    }

    private async Task<string> ConnectAsync(string[] args, CancellationToken ct)
    {
        if (args.Length < 1) return "Usage: /mcp connect <name>";
        var name = args[0];
        try
        {
            var ok = await connectionManager.ConnectOneAsync(name, ct).ConfigureAwait(false);
            if (ok)
            {
                logger.LogInformation("Connected to MCP server: {Name}", name);
                return $"MCP server '{name}' connected.";
            }
            return $"MCP server '{name}' not found in configuration or connection failed. Use /mcp list to see configured servers.";
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to connect MCP server: {Name}", name);
            return $"Failed to connect MCP server '{name}': {ex.Message}";
        }
    }

    private async Task<string> DisconnectAsync(string[] args, CancellationToken ct)
    {
        if (args.Length < 1) return "Usage: /mcp disconnect <name>";
        var name = args[0];
        try
        {
            await connectionManager.DisconnectAsync(name).ConfigureAwait(false);
            return $"MCP server '{name}' disconnected.";
        }
        catch (Exception ex)
        {
            return $"Failed to disconnect '{name}': {ex.Message}";
        }
    }

    // enable / disable

    private async Task<string> ToggleEnabledAsync(string[] args, bool enabled, CancellationToken ct)
    {
        if (args.Length < 1) return $"Usage: /mcp {(enabled ? "enable" : "disable")} <name>";
        var name = args[0];
        foreach (var scope in new[] { "project", "user" })
        {
            var path = GetConfigPath(scope);
            if (!File.Exists(path)) continue;
            var file = LoadConfigFile(path);
            if (file?.McpServers.TryGetValue(name, out var entry) == true)
            {
                file.McpServers[name] = entry with { Disabled = enabled ? null : true };
                await SaveConfigFileAsync(path, file, ct).ConfigureAwait(false);

                // Disabling also drops the runtime connection; enabling does not auto-connect
                // (use /mcp connect to bring it up on demand).
                if (!enabled)
                {
                    try { await connectionManager.DisconnectAsync(name).ConfigureAwait(false); }
                    catch (Exception ex) { logger.LogWarning(ex, "Error disconnecting '{Name}' on disable", name); }
                }
                return $"MCP server '{name}' {(enabled ? "enabled" : "disabled")}.";
            }
        }
        return $"MCP server '{name}' not found.";
    }

    // config path + file I/O

    private static string GetConfigPath(string scope) =>
        scope == "project"
            ? Path.Combine(Directory.GetCurrentDirectory(), ".mcp.json")
            : McpMultiScopeConfigLoader.GetUserConfigPath();

    private McpConfigFile? LoadConfigFile(string path)
    {
        try
        {
            return !File.Exists(path) ? null : JsonSerializer.Deserialize<McpConfigFile>(File.ReadAllText(path), JsonOpts);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to load MCP config from {Path}", path);
            return null;
        }
    }

    private static async Task SaveConfigFileAsync(string path, McpConfigFile config, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(config, JsonOpts);
        await File.WriteAllTextAsync(path, json, ct).ConfigureAwait(false);
    }
}
