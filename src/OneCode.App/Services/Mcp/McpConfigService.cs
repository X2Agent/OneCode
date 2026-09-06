using OneCode.Core.Mcp;
using OneCode.Infrastructure.Mcp;
using OneCode.App.Tui;

namespace OneCode.App.Services.Mcp;

/// <summary>
/// MCP 工具白名单配置页的数据准备与保存落地。
/// <see cref="PrepareAsync"/> 产出 overlay 快照（配置定义 + 已连接服务器的方法清单）；
/// <see cref="ApplyAsync"/> 把保存结果写回 .mcp.json（project → user 作用域），
/// 并对已连接服务器热生效（禁用断开连接，白名单变化走 ReloadToolsAsync）。
/// </summary>
public sealed class McpConfigService(
    McpMultiScopeConfigLoader configLoader,
    IMcpConnectionManager connectionManager,
    ILogger<McpConfigService> logger)
{
    /// <summary>构建配置页快照：每个配置服务器的启用/连接状态与其暴露的方法名列表。</summary>
    public async Task<IReadOnlyList<McpConfigServerEntry>> PrepareAsync(CancellationToken ct = default)
    {
        var merged = await configLoader.LoadAllAsync(ct: ct).ConfigureAwait(false);
        var statuses = connectionManager.GetStatus()
            .ToDictionary(s => s.Name, StringComparer.OrdinalIgnoreCase);

        var entries = new List<McpConfigServerEntry>();
        foreach (var (name, def) in merged.Servers.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
        {
            var connected = statuses.TryGetValue(name, out var st) && st.IsConnected;
            var liveTools = await ListLiveToolsAsync(name, connected, ct).ConfigureAwait(false);
            entries.Add(new McpConfigServerEntry(
                Name: name,
                Disabled: def.Disabled,
                Connected: connected && liveTools.Count > 0,
                EnabledTools: def.EnabledTools,
                LiveTools: liveTools));
        }

        return entries;
    }

    /// <summary>
    /// 落地配置页保存结果。只写回 project/user 配置中实际存在的服务器条目
    /// （内置服务不落盘——写入会以残缺条目覆盖内置定义）；禁用断开连接，
    /// 白名单变化热生效。返回给用户看的摘要文本。
    /// </summary>
    public async Task<string> ApplyAsync(McpConfigResult result, CancellationToken ct = default)
    {
        var notes = new List<string>();
        foreach (var change in result.Servers)
        {
            var saved = await SaveToConfigFileAsync(change, ct).ConfigureAwait(false);
            if (!saved)
            {
                notes.Add($"'{change.Name}'：未在 project/user 配置中找到（内置服务不持久化），已跳过");
                continue;
            }

            if (change.Disabled == true)
            {
                // 与 /mcp disable 语义一致：禁用同时摘除运行时连接。
                try
                {
                    await connectionManager.DisconnectAsync(change.Name).ConfigureAwait(false);
                    notes.Add($"'{change.Name}'：已禁用并断开连接");
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to disconnect '{Name}' after disabling", change.Name);
                    notes.Add($"'{change.Name}'：已禁用（断开连接失败，可手动 /mcp disconnect）");
                }
                continue;
            }

            if (change.EnabledTools is not null)
            {
                try
                {
                    var reloaded = await connectionManager.ReloadToolsAsync(change.Name, ct).ConfigureAwait(false);
                    notes.Add(reloaded
                        ? $"'{change.Name}'：白名单已热生效（{change.EnabledTools.Count} 个方法）"
                        : $"'{change.Name}'：白名单已保存（未连接，连接后生效）");
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to hot-reload tools for '{Name}'", change.Name);
                    notes.Add($"'{change.Name}'：白名单已保存（热生效失败，可 /mcp connect 后重试）");
                }
            }
        }

        return notes.Count == 0 ? "MCP 配置未变更。" : "MCP 配置已保存：\n  " + string.Join("\n  ", notes);
    }

    private async Task<IReadOnlyList<string>> ListLiveToolsAsync(string name, bool connected, CancellationToken ct)
    {
        if (!connected)
            return [];

        try
        {
            var client = connectionManager.GetClient(name);
            if (client?.IsConnected != true)
                return [];

            var tools = await client.ListToolsAsync(ct).ConfigureAwait(false);
            return tools.Select(t => t.Name).ToList();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to list tools of MCP server '{Name}'", name);
            return [];
        }
    }

    private async Task<bool> SaveToConfigFileAsync(McpConfigServerChange change, CancellationToken ct)
    {
        foreach (var scope in new[] { "project", "user" })
        {
            var path = McpConfigFileStore.GetPath(scope);
            var file = McpConfigFileStore.Load(path);
            if (file is null || !file.McpServers.TryGetValue(change.Name, out var entry))
                continue;

            var updated = entry;
            if (change.Disabled is { } disabled)
                updated = updated with { Disabled = disabled ? true : null };
            if (change.EnabledTools is { } tools)
                updated = updated with { Tools = [.. tools] };

            file.McpServers[change.Name] = updated;
            await McpConfigFileStore.SaveAsync(path, file, ct).ConfigureAwait(false);
            return true;
        }

        return false;
    }
}