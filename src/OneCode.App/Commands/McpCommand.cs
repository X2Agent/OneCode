using OneCode.Core.Mcp;
using System.Text;
using OneCode.Infrastructure.Mcp;

namespace OneCode.App.Commands;

/// <summary>
/// Manages MCP servers: discover (search) and install from the official MCP registry
/// (registry.modelcontextprotocol.io), add/remove/enable/disable local config, and
/// connect/disconnect at runtime.
/// All runtime state flows through <see cref="IMcpConnectionManager"/> — the single
/// source of truth that also feeds the LLM tool catalog, so connect/disconnect
/// here immediately affects which tools the model can call.
/// Config-file persistence lives in <see cref="McpConfigFileStore"/>.
/// </summary>
public sealed partial class McpCommand(
    IMcpConnectionManager connectionManager,
    OfficialMcpRegistryClient registryClient,
    McpMultiScopeConfigLoader configLoader,
    ILogger<McpCommand> logger) : Command
{
    public override string Name => "mcp";
    public override string Description => "Manage MCP server connections";
    public override CommandCategory Category => CommandCategory.Skill;
    public override string? ArgumentHint => "[list|get|tools|search|install|add|remove|connect|disconnect|enable|disable|enable-tool|disable-tool] <args>";

    // 最近一次 /mcp search（或 install 短名歧义提示）展示的编号列表；
    // /mcp install <n> 据此把编号还原为完整限定名（仅作名字快捷方式，安装时仍拉取最新元数据）。
    // 命令在 TUI / headless 路径均串行执行（SlashCommandPipeline 单例顺序 await），无并发写风险。
    private IReadOnlyList<OfficialRegistryServer> _lastSearchResults = [];

    // 官方 registry 的 search 端点较慢（实测 18~26s 返回），执行期间 AgentStatusBar 必须给出
    // 动态忙碌反馈，否则用户会误以为卡死。install 按目标形态细分：序号/限定名只做一次
    // GetLatest 元数据解析（秒级），短名可能回退一次慢速 registry search（见 ResolveInstallTargetAsync），
    // 沿用 search 文案让等待预期一致。其余子命令为本地操作、瞬时完成，沿用默认标签。
    public override string? GetSubcommandProgressMessage(string[] args) =>
        args.Length > 0 ? args[0].ToLowerInvariant() switch
        {
            "search" => "searching MCP registry",
            "install" when args.Length > 1 && (args[1].All(char.IsAsciiDigit) || args[1].Contains('/'))
                => "installing from MCP registry",
            "install" when args.Length > 1 => "searching MCP registry",
            _ => null,
        }
        : null;

    public override async Task<CommandResult> ExecuteAsync(string[] args, CancellationToken ct = default)
    {
        if (args.Length == 0) return CommandResult.Text(await ListServersAsync(ct));

        return args[0].ToLowerInvariant() switch
        {
            "list" or "ls" => CommandResult.Text(await ListServersAsync(ct)),
            "get" when args.Length > 1 => CommandResult.Text(await GetServerAsync(args[1], ct)),
            "tools" when args.Length > 1 => CommandResult.Text(await ListToolsStatusAsync(args[1], ct)),
            "search" when args.Length > 1 => CommandResult.Text(await SearchAsync(args[1..], ct)),
            "install" when args.Length > 1 => CommandResult.Text(await InstallAsync(args[1..], ct)),
            "add" => CommandResult.Text(await AddServerAsync(args[1..], ct)),
            "remove" or "rm" => CommandResult.Text(await RemoveServerAsync(args[1..], ct)),
            "connect" => CommandResult.Text(await ConnectAsync(args[1..], ct)),
            "disconnect" => CommandResult.Text(await DisconnectAsync(args[1..], ct)),
            "enable" => CommandResult.Text(await ToggleEnabledAsync(args[1..], true, ct)),
            "disable" => CommandResult.Text(await ToggleEnabledAsync(args[1..], false, ct)),
            "enable-tool" => CommandResult.Text(await ToggleToolAsync(args[1..], true, ct)),
            "disable-tool" => CommandResult.Text(await ToggleToolAsync(args[1..], false, ct)),
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
                // Plan C：断连原因始终可见——坏服务器不再只是一个静默的 [disconnected]。
                if (!connected && status.TryGetValue(name, out var failed) && failed.LastError is { } reason)
                    sb.AppendLine(CultureInfo.InvariantCulture, $"    └ 失败原因：{reason}（用 /mcp connect {name} 重试）");
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
        if (def.EnabledTools is { } whitelist)
        {
            sb.AppendLine(whitelist.Count == 0
                ? "Tool filter: [] (no tools exposed)"
                : $"Tool filter: {whitelist.Count} pattern(s): {string.Join(", ", whitelist)}");
        }
        else
        {
            sb.AppendLine("Tool filter: none (all tools exposed)");
        }

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

        IReadOnlyList<OfficialRegistryServer> results;
        try
        {
            results = await registryClient.SearchAsync(query, limit: 20, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // 官方 registry search 端点可用但响应慢（实测 18~26s）：超时按可重试的网络问题提示
            return "MCP registry search timed out — the official registry search endpoint is slow at the moment; check network/proxy and retry.";
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "MCP registry search failed for '{Query}'", query);
            return $"MCP registry search failed: {ex.Message}";
        }

        if (results.Count == 0)
        {
            // 空结果清空缓存：编号永远对应用户最近看到的列表，避免引用过期展示
            _lastSearchResults = [];
            return $"No MCP servers found for '{query}'.";
        }

        // 缓存本列表，供 '/mcp install <序号>' 按编号引用
        _lastSearchResults = results;

        var sb = new StringBuilder($"Registry search: '{query}' ({results.Count} results)\n\n");
        for (var i = 0; i < results.Count; i++)
        {
            var s = results[i];
            var local = s.Packages is { Count: > 0 } ? "stdio" : "remote";
            var dep = s.IsDeprecated ? ", deprecated" : "";
            sb.AppendLine(CultureInfo.InvariantCulture, $"  [{i + 1}] {s.Name,-34} ({local}{dep})");
            if (!string.IsNullOrWhiteSpace(s.Title) && s.Title != s.Name)
                sb.AppendLine(CultureInfo.InvariantCulture, $"    {s.Title}");
            if (!string.IsNullOrWhiteSpace(s.Description))
            {
                var desc = s.Description.Length > 90 ? s.Description[..90] + "…" : s.Description;
                sb.AppendLine(CultureInfo.InvariantCulture, $"    {desc}");
            }
        }
        sb.AppendLine();
        sb.AppendLine("Use '/mcp install <n>' to install a numbered server (e.g. '/mcp install 1'), or '/mcp install <name>'.");
        sb.AppendLine("Remote servers have no installable package — use '/mcp add <name> --transport http --url <url>' instead.");
        return sb.ToString().TrimEnd();
    }

    // install

    /// <summary>
    /// 写操作前的配置加载：文件损坏时返回失败与用户可操作指引（绝不静默按"不存在"处理，
    /// 防止随后的 SaveAsync 覆盖用户原有配置）。
    /// </summary>
    private static bool TryLoadConfigForWrite(
        string path,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out McpConfigFile? file,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(false)] out string? error)
    {
        try
        {
            file = McpConfigFileStore.Load(path) ?? new McpConfigFile();
            error = null;
            return true;
        }
        catch (InvalidOperationException ex)
        {
            file = null;
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// 解析 --scope 参数为合法的配置文件作用域（project/user）。
    /// 非法值返回错误而非经 <see cref="McpConfigFileStore.GetPath"/> 静默落到 user 作用域。
    /// </summary>
    private static bool TryParseScope(
        string? scope,
        out string normalized,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(false)] out string? error)
    {
        normalized = (scope ?? "project").ToLowerInvariant();
        if (normalized is "project" or "user")
        {
            error = null;
            return true;
        }

        error = $"Invalid scope '{scope}'. Use 'project' or 'user'.";
        return false;
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

        if (!TryParseScope(scope, out var addScope, out var addScopeError))
            return addScopeError;

        // 校验传输必需参数，避免写入连接时必然失败的无效配置（connect 阶段 def.Url! 会 NRE）。
        if (normalized is "http" or "sse" or "ws")
        {
            if (string.IsNullOrWhiteSpace(url))
                return $"Transport '{normalized}' requires --url.";
        }
        else if (normalized == "stdio" && string.IsNullOrWhiteSpace(command))
        {
            return "Transport 'stdio' requires --command.";
        }

        var entry = new McpConfigEntry
        {
            Type = normalized,
            Command = command,
            Args = cmdArgs.Count > 0 ? cmdArgs.ToArray() : null,
            Url = url,
        };

        var configPath = McpConfigFileStore.GetPath(addScope);
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        if (!TryLoadConfigForWrite(configPath, out var addFile, out var addError))
            return $"Cannot add '{name}': {addError}";
        addFile.McpServers[name] = entry;
        await McpConfigFileStore.SaveAsync(configPath, addFile, ct).ConfigureAwait(false);
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
            var path = McpConfigFileStore.GetPath(scope);
            if (!File.Exists(path)) continue;
            if (!TryLoadConfigForWrite(path, out var removeFile, out var removeError))
                return $"Cannot update {scope} config for '{name}': {removeError}";
            var file = removeFile;
            if (file?.McpServers.Remove(name) == true)
            {
                await McpConfigFileStore.SaveAsync(path, file, ct).ConfigureAwait(false);
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

            // Plan C：失败必须给出原因——此前只回 "not found in configuration or
            // connection failed"，用户分不清"没配置"与"连不上"。
            var reason = connectionManager.GetStatus()
                .FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase))?.LastError;
            return reason is null
                ? $"MCP server '{name}' not found in configuration or connection failed. Use /mcp list to see configured servers."
                : $"MCP server '{name}' connection failed: {reason} — use /mcp list to see configured servers.";
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return $"MCP server '{name}' connection timed out — check the server command/URL, or raise initTimeoutMs in .mcp.json.";
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
            var path = McpConfigFileStore.GetPath(scope);
            if (!File.Exists(path)) continue;
            if (!TryLoadConfigForWrite(path, out var toggleFile, out var toggleError))
                return $"Cannot update {scope} config for '{name}': {toggleError}";
            var file = toggleFile;
            if (file?.McpServers.TryGetValue(name, out var entry) == true)
            {
                file.McpServers[name] = entry with { Disabled = enabled ? null : true };
                await McpConfigFileStore.SaveAsync(path, file, ct).ConfigureAwait(false);

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
}
