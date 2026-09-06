using System.Text;
using OneCode.Infrastructure.Mcp;

namespace OneCode.App.Commands;

// /mcp install：从官方 registry 安装服务器为本地 stdio。
// 目标解析（序号 / 完整限定名 / 短名回退）在 ResolveInstallTargetAsync，
// 纯函数内核在 InstallTargetResolver（无 I/O，可独立单测）。
public sealed partial class McpCommand
{
    private async Task<string> InstallAsync(string[] args, CancellationToken ct)
    {
        // /mcp install <qualifiedName|序号|短名> [--name <name>] [--scope project|user] [--no-connect]
        var resolution = await ResolveInstallTargetAsync(args[0], ct).ConfigureAwait(false);
        if (resolution.Error is not null)
            return resolution.Error;
        var qualifiedName = resolution.QualifiedName!;

        string? customName = null;
        var scope = "project";
        // 安装后默认立即连接验证（Plan E）：装坏的包（pypi 包名不存在、网络不可达）
        // 在安装反馈里当场暴露，而不是等到下次启动时静默失败；--no-connect 跳过。
        var connect = true;
        for (var i = 1; i < args.Length; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--name": if (++i < args.Length) customName = args[i]; break;
                case "--scope": if (++i < args.Length) scope = args[i]; break;
                case "--no-connect": connect = false; break;
            }
        }

        if (!TryParseScope(scope, out var installScope, out var installScopeError))
            return installScopeError;

        OfficialRegistryServer? server;
        try
        {
            server = await registryClient.GetLatestAsync(qualifiedName, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return $"MCP registry request timed out while resolving '{qualifiedName}' — check network/proxy and retry.";
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to resolve '{Name}' from official registry", qualifiedName);
            return $"Failed to reach the MCP registry: {ex.Message}";
        }

        if (server is null)
            return $"'{qualifiedName}' not found in the registry — verify the name via '/mcp search {qualifiedName.Split('/')[0]}'.";

        var entry = TryBuildLocalEntry(server);
        if (entry is null)
        {
            var hint = server.Remotes is { Count: > 0 }
                ? " (it is a remote-only server). Use '/mcp add' to configure the remote URL manually."
                : ". Use '/mcp add' to configure manually.";
            return $"'{qualifiedName}' has no installable local package{hint}";
        }

        var name = customName ?? qualifiedName.Replace('/', '-');

        var configPath = McpConfigFileStore.GetPath(installScope);
        // 目录创建已下沉到 McpConfigFileStore.SaveAsync（App 层不做直接文件 I/O）。
        if (!TryLoadConfigForWrite(configPath, out var file, out var installError))
            return $"Cannot install '{name}': {installError}";
        file.McpServers[name] = entry;
        await McpConfigFileStore.SaveAsync(configPath, file, ct).ConfigureAwait(false);

        logger.LogInformation("Installed MCP server '{Name}' from registry", name);

        var commandDesc = $"{entry.Command} {string.Join(" ", entry.Args ?? [])}".TrimEnd();
        var msg = $"Installed '{name}' ({server.Title ?? server.Name}) → {installScope} config (stdio: {commandDesc}).";

        if (connect)
        {
            try
            {
                var ok = await connectionManager.ConnectOneAsync(name, ct).ConfigureAwait(false);
                if (ok)
                {
                    msg += " Connected.";
                }
                else
                {
                    // Plan E：安装即验证——失败当场给出原因（pypi 包名不存在、网络不可达等），
                    // 不让坏包静默存活到下次启动。
                    var reason = connectionManager.GetStatus()
                        .FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase))?.LastError;
                    msg += reason is null
                        ? $" Connection failed — use '/mcp connect {name}' to retry."
                        : $" Connection failed: {reason} — use '/mcp connect {name}' to retry.";
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                msg += $" Connection timed out — use '/mcp connect {name}' to retry.";
            }
        }
        else
        {
            msg += $" Use '/mcp connect {name}' to connect.";
        }

        return msg;
    }

    /// <summary>
    /// install 目标三态归一化：① 纯数字 → 引用最近一次 <c>/mcp search</c> 的编号列表（仅作为名字快捷方式，
    /// 随后仍走 GetLatestAsync 拉取最新元数据，不消费缓存内容）；② 含 '/' → 完整限定名原样；③ 其余（短名）→
    /// registry search 按 server name 回退消歧（单查端点对无 '/' 的名字必然 404，故直接 search，一次请求）。
    /// 短名多命中时把候选列表写入缓存并返回编号提示，用户以 <c>/mcp install &lt;n&gt;</c> 二次选择。
    /// </summary>
    internal async Task<InstallTargetResolution> ResolveInstallTargetAsync(string raw, CancellationToken ct)
    {
        // ① 序号：解析为编号列表中的完整限定名
        if (InstallTargetResolver.TryParseIndex(raw, _lastSearchResults.Count, out var index, out var indexError))
        {
            if (indexError is not null)
                return InstallTargetResolution.Fail(indexError);
            return InstallTargetResolution.FromName(_lastSearchResults[index - 1].Name);
        }

        // ② 完整限定名（reverse-DNS 规范必含 '/'）——按现状直接安装
        if (raw.Contains('/'))
            return InstallTargetResolution.FromName(raw);

        // ③ 短名回退：服务端 search（结果即"名字相关"的候选），再按 server name 本地过滤
        IReadOnlyList<OfficialRegistryServer> results;
        try
        {
            results = await registryClient.SearchAsync(raw, limit: 20, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return InstallTargetResolution.Fail($"MCP registry request timed out while resolving '{raw}' — check network/proxy and retry.");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to resolve '{Name}' via registry search fallback", raw);
            return InstallTargetResolution.Fail($"Failed to reach the MCP registry: {ex.Message}");
        }

        if (InstallTargetResolver.TryPickShortNameMatch(raw, results, out var unique, out var candidates))
            return InstallTargetResolution.FromName(unique!.Name);

        if (candidates.Count > 0)
        {
            // 歧义：候选列表写入缓存，保证提示中的编号可直接回选（编号永远指最近展示的列表）
            _lastSearchResults = candidates;

            var sb = new StringBuilder($"'{raw}' is ambiguous — {candidates.Count} servers match. Install one with '/mcp install <n>':\n");
            for (var i = 0; i < candidates.Count; i++)
            {
                var s = candidates[i];
                var local = s.Packages is { Count: > 0 } ? "stdio" : "remote";
                sb.AppendLine(CultureInfo.InvariantCulture, $"  [{i + 1}] {s.Name,-36} ({local})");
            }
            sb.AppendLine();
            sb.Append("Installable targets must be stdio servers — remote ones need '/mcp add <name> --transport http --url <url>'.");
            return InstallTargetResolution.Fail(sb.ToString());
        }

        return InstallTargetResolution.Fail($"'{raw}' not found in the registry — verify the name via '/mcp search {raw}'.");
    }

    /// <summary>
    /// 将官方 registry 的 server 元数据映射为本地 stdio 配置：
    /// npm → npx -y {identifier}；pypi → uvx {identifier}；oci → docker run -i --rm {identifier}。
    /// nuget/mcpb 暂不支持（返回 null，由调用方提示手动配置）。
    /// </summary>
    private static McpConfigEntry? TryBuildLocalEntry(OfficialRegistryServer server)
    {
        foreach (var pkg in server.Packages ?? [])
        {
            switch (pkg.RegistryType.ToLowerInvariant())
            {
                case "npm":
                    return new McpConfigEntry { Type = "stdio", Command = "npx", Args = ["-y", pkg.Identifier] };
                case "pypi":
                    return new McpConfigEntry { Type = "stdio", Command = "uvx", Args = [pkg.Identifier] };
                case "oci":
                    return new McpConfigEntry { Type = "stdio", Command = "docker", Args = ["run", "-i", "--rm", pkg.Identifier] };
            }
        }

        return null;
    }
}

/// <summary>install 目标解析结果：QualifiedName 非空 = 解析成功；Error 非空 = 终态错误文案（已含用户指引）。</summary>
internal sealed record InstallTargetResolution(string? QualifiedName, string? Error)
{
    public static InstallTargetResolution FromName(string name) => new(name, null);

    public static InstallTargetResolution Fail(string error) => new(null, error);
}

/// <summary>
/// <c>/mcp install</c> 目标解析的纯函数内核（无 I/O，可独立单测）。
/// 匹配规则收紧为 <b>server name 子串</b>（大小写不敏感，Ordinal）：完整名包含短名即命中。
/// title / description 一律不参与安装决策——按"介绍里提到"就装包是危险动作。
/// </summary>
internal static class InstallTargetResolver
{
    /// <summary>
    /// 解析序号语法。返回 false 表示输入不是序号（应继续按限定名/短名处理）；
    /// 返回 true 且 index &gt; 0 = 有效引用；返回 true 且 error 非空 = 序号无效/越界/无缓存的终态错误。
    /// </summary>
    public static bool TryParseIndex(string raw, int cacheCount, out int index, out string? error)
    {
        index = 0;
        error = null;

        // 序号 = 纯 ASCII 数字；'+/-' 前缀、含字母均视为非序号语法
        if (raw.Length == 0 || !raw.All(char.IsAsciiDigit))
            return false;

        if (!uint.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n == 0)
        {
            error = $"'{raw}' is not a valid result number (1-based).";
            return true;
        }

        if (cacheCount == 0)
        {
            error = "No search results to reference — run '/mcp search <query>' first, then install by number.";
            return true;
        }

        if (n > (uint)cacheCount)
        {
            error = $"Result number {n} is out of range — the last search returned {cacheCount} result(s). Run '/mcp search <query>' again to refresh.";
            return true;
        }

        index = (int)n;
        return true;
    }

    /// <summary>
    /// 短名回退消歧：唯一命中 → <paramref name="unique"/> 自动采用（可能是 remote-only，
    /// 由后续安装流程给出 '/mcp add' 指引）；多命中 → <paramref name="candidates"/> 交还用户编号选择；
    /// 无命中 → candidates 为空、unique 为 null。
    /// </summary>
    public static bool TryPickShortNameMatch(
        string shortName,
        IReadOnlyList<OfficialRegistryServer> results,
        out OfficialRegistryServer? unique,
        out IReadOnlyList<OfficialRegistryServer> candidates)
    {
        var matches = results
            .Where(s => s.Name.Contains(shortName, StringComparison.OrdinalIgnoreCase))
            .ToList();
        unique = matches.Count == 1 ? matches[0] : null;
        candidates = matches;
        return unique is not null;
    }
}
