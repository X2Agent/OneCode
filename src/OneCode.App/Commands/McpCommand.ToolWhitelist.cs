using OneCode.Core.Mcp;
using System.Text;
using OneCode.Infrastructure.Mcp;

namespace OneCode.App.Commands;

// MCP 工具白名单子命令（tools / enable-tool / disable-tool）。
// 仅作用于 MCP 服务器提供的方法——内置静态工具不经过此过滤器。
public sealed partial class McpCommand
{
    // tool-level selection（仅作用于 MCP 服务器提供的方法，内置静态工具不在此列）

    /// <summary>
    /// 列出某服务器暴露的全部方法及其勾选状态（✓ = 会提供给模型，✗ = 被白名单过滤）。
    /// 需要服务器处于已连接状态（方法列表来自运行时的 ListTools）。
    /// </summary>
    private async Task<string> ListToolsStatusAsync(string name, CancellationToken ct)
    {
        var merged = await configLoader.LoadAllAsync(ct: ct).ConfigureAwait(false);
        if (!merged.Servers.TryGetValue(name, out var def))
            return $"MCP server '{name}' not found in configuration. Use /mcp list to see all servers.";

        var client = connectionManager.GetClient(name);
        if (client?.IsConnected != true)
            return $"MCP server '{name}' is not connected. Use /mcp connect {name} first.";

        IReadOnlyList<McpTool> tools;
        try
        {
            tools = await client.ListToolsAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return $"Could not list tools for '{name}': {ex.Message}";
        }

        var enabledCount = tools.Count(t => McpToolFilter.IsEnabled(def, t.Name));
        var filterDesc = def.EnabledTools is null
            ? "no whitelist (all enabled)"
            : def.EnabledTools.Count == 0
                ? "whitelist empty (all disabled)"
                : $"whitelist: {string.Join(", ", def.EnabledTools)}";

        var sb = new StringBuilder();
        sb.AppendFormat(CultureInfo.InvariantCulture,
            "{0} ({1} tools, {2} enabled) — {3}\n", name, tools.Count, enabledCount, filterDesc);
        foreach (var tool in tools.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase))
        {
            var mark = McpToolFilter.IsEnabled(def, tool.Name) ? "✓" : "✗";
            sb.AppendLine(CultureInfo.InvariantCulture, $"  {mark} {tool.Name}");
        }
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"Use '/mcp enable-tool {name} <pattern>' or '/mcp disable-tool {name} <pattern>' (wildcard * supported).");
        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// 启用/禁用某服务器的一个方法（支持 * 通配符）。写回配置文件并热生效（不断开连接）。
    /// 白名单从未配置（null，全启用）时首次禁用会先把服务器当前暴露的全部方法物化进白名单，
    /// 再移除目标——避免"禁用一个"被解释成"只启用一个"。
    /// </summary>
    private async Task<string> ToggleToolAsync(string[] args, bool enable, CancellationToken ct)
    {
        if (args.Length < 2)
            return $"Usage: /mcp {(enable ? "enable-tool" : "disable-tool")} <server> <tool-pattern>";

        var name = args[0];
        var pattern = args[1];

        var client = connectionManager.GetClient(name);
        if (client?.IsConnected != true)
            return $"MCP server '{name}' is not connected. Use /mcp connect {name} first.";

        foreach (var scope in new[] { "project", "user" })
        {
            // 文件不存在时 TryLoadConfigForWrite 返回空配置——TryGetValue 不命中自然跳过该作用域。
            var path = McpConfigFileStore.GetPath(scope);
            if (!TryLoadConfigForWrite(path, out var loadFile, out var loadError))
                return $"Cannot update {scope} config for '{name}': {loadError}";
            if (loadFile?.McpServers.TryGetValue(name, out var found) == true)
            {
                var updated = await UpdateToolWhitelistAsync(
                    path, loadFile, name, found, pattern, enable, ct).ConfigureAwait(false);
                if (updated is not null)
                    return updated;
            }
        }

        return $"MCP server '{name}' not found in project or user config. Use '/mcp add {name} ...' first.";
    }

    /// <summary>
    /// 计算并写回新的工具白名单，然后热生效（不断开连接）。返回结果消息；
    /// null 表示配置文件中没有该服务器条目（例如只存在于内置清单）。
    /// </summary>
    private async Task<string?> UpdateToolWhitelistAsync(
        string path,
        McpConfigFile file,
        string name,
        McpConfigEntry entry,
        string pattern,
        bool enable,
        CancellationToken ct)
    {
        var client = connectionManager.GetClient(name);
        if (client?.IsConnected != true)
            return null;

        IReadOnlyList<McpTool> liveTools;
        try
        {
            liveTools = await client.ListToolsAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return $"Could not list tools for '{name}': {ex.Message}";
        }

        // 物化当前白名单：null（未配置 = 全启用）→ 展开为服务器现有全部方法名。
        var whitelist = entry.Tools is null
            ? liveTools.Select(t => t.Name).ToList()
            : entry.Tools.ToList();

        if (enable)
        {
            if (!whitelist.Contains(pattern, StringComparer.OrdinalIgnoreCase))
                whitelist.Add(pattern);
        }
        else
        {
            // 先精确移除；再移除"能被 pattern 覆盖的实际方法名"，但不动用户显式写下的
            // 通配符条目（disable browser_click 不应吞掉 browser_*）。
            whitelist.RemoveAll(p => string.Equals(p, pattern, StringComparison.OrdinalIgnoreCase));
            if (pattern.Contains('*'))
            {
                whitelist.RemoveAll(p => liveTools.Any(t =>
                    string.Equals(p, t.Name, StringComparison.Ordinal)
                    && McpToolFilter.Matches(pattern, t.Name)));
            }

            // pattern 仍被某个通配符条目覆盖时（如白名单里的 browser_* 盖住了要禁用的
            // browser_click），仅精确移除不会生效——本次 disable 成为静默 no-op，而结果
            // 消息却宣称已禁用。把覆盖条目物化为“命中的实际方法 - pattern 命中的方法”，
            // 禁用语义才真正落地（代价：通配符不再覆盖服务器后续新增方法，与首次禁用
            // 物化全部方法的既有行为一致）。
            var covering = whitelist
                .Where(p => !string.Equals(p, pattern, StringComparison.OrdinalIgnoreCase)
                            && p.Contains('*')
                            && McpToolFilter.Matches(p, pattern))
                .ToList();
            foreach (var wildcard in covering)
            {
                whitelist.Remove(wildcard);
                foreach (var tool in liveTools)
                {
                    if (McpToolFilter.Matches(wildcard, tool.Name)
                        && !McpToolFilter.Matches(pattern, tool.Name)
                        && !whitelist.Contains(tool.Name, StringComparer.OrdinalIgnoreCase))
                    {
                        whitelist.Add(tool.Name);
                    }
                }
            }
        }

        entry = entry with { Tools = [.. whitelist] };
        file.McpServers[name] = entry;
        await McpConfigFileStore.SaveAsync(path, file, ct).ConfigureAwait(false);

        // 热生效：重载连接池中的 AgentTools（不断开连接），catalog 下一轮自动拿到新名单。
        var reloaded = false;
        try
        {
            reloaded = await connectionManager.ReloadToolsAsync(name, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to hot-reload tools for '{Name}'", name);
        }

        var enabledCount = liveTools.Count(t => McpToolFilter.IsEnabled(entry.Tools, t.Name));
        var suffix = reloaded ? " (applied)" : " (use /mcp connect to apply)";
        return $"'{pattern}' {(enable ? "enabled" : "disabled")} for '{name}' — " +
               $"{enabledCount}/{liveTools.Count} tools enabled{suffix}";
    }
}
