using OneCode.Core.Mcp;
using OneCode.Infrastructure.Mcp;

namespace OneCode.App.Services.Mcp;

/// <summary>
/// MCP 启动连接编排器：驱动整个启动连接阶段——加载多作用域配置、剔除禁用与按需
/// 连接的内置服务器、在启动总预算内以每服务器独立超时窗口并发连接，并把失败汇总
/// 为一条 startup hint。单台服务器的实际连接与失败落池由
/// <see cref="McpConnectionManager"/> 的连接池与门闩承载（软失败语义）。
/// </summary>
internal sealed class McpStartupConnector(
    McpConnectionManager connectionManager,
    McpMultiScopeConfigLoader multiScopeLoader,
    IStartupHintCollector? hintCollector,
    ILogger logger)
{
    /// <summary>
    /// 整个启动连接阶段的总预算（60 秒）。每台服务器各自享有独立超时窗口（默认 30 秒、
    /// 配置可覆盖——避免一台慢服务器吃掉全局超时），总体仍受该预算约束防止启动被无限拖住。
    /// </summary>
    private static readonly TimeSpan StartupBudgetTimeout = TimeSpan.FromSeconds(60);

    // 最近一次启动连接的启用服务器目标数（状态栏"连接中 x/y"的分母）。
    private volatile int _expectedCount;

    /// <summary>最近一次启动连接的启用服务器目标数（状态栏"连接中 x/y"的分母）。</summary>
    internal int ExpectedCount => _expectedCount;

    /// <summary>Connect to every enabled server found in the multi-scope config files (startup use).</summary>
    public async Task ConnectAllAsync(CancellationToken ct = default)
    {
        var merged = await multiScopeLoader.LoadAllAsync(ct: ct).ConfigureAwait(false);
        var enabled = merged.Servers.Where(kv => !kv.Value.Disabled).ToDictionary(kv => kv.Key, kv => kv.Value);
        var disabledCount = merged.Servers.Count(kv => kv.Value.Disabled);

        if (disabledCount > 0)
            logger.LogInformation("Skipping {Count} disabled MCP server(s).", disabledCount);

        // 内置服务（如 playwright）默认按需连接：不随启动连接，由消费方
        // 在首次使用时通过 ConnectOneAsync 触发。连接时机是"正向约定"，
        // 无需用户在配置里表达。
        var builtIn = enabled.Where(kv => BuiltInMcpServers.IsBuiltIn(kv.Key)).Select(kv => kv.Key).ToList();
        if (builtIn.Count > 0)
        {
            logger.LogDebug("Deferring {Count} built-in MCP server(s) (on-demand): {Names}", builtIn.Count, string.Join(", ", builtIn));
            enabled = enabled.Where(kv => !BuiltInMcpServers.IsBuiltIn(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value);
        }

        if (enabled.Count == 0)
        {
            logger.LogDebug("No MCP servers to connect at startup.");
            _expectedCount = 0;
            return;
        }

        // 记录目标数（状态栏三态"连接中 x/y"的分母）。内置按需服务已被剔除，不计入。
        _expectedCount = enabled.Count;
        logger.LogInformation("Connecting to {Count} MCP server(s)...", enabled.Count);

        using var startupBudget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        startupBudget.CancelAfter(StartupBudgetTimeout);

        // 并发连接（与既有行为一致），但每台服务器持有独立的超时窗口（配置
        // initTimeoutMs / startupTimeoutMs 可覆盖，未配置回落 30s），链接到启动总预算：
        // 任一服务器超时不影响其他服务器的连接进度。
        var failures = new ConcurrentBag<string>();
        await Task.WhenAll(enabled.Select(async kv =>
        {
            using var perServer = CancellationTokenSource.CreateLinkedTokenSource(ct, startupBudget.Token);
            perServer.CancelAfter(McpConnectionManager.ResolveConnectionTimeout(kv.Value));
            await ConnectOneWithTimeoutAsync(kv.Key, kv.Value, perServer.Token, failures).ConfigureAwait(false);
        })).ConfigureAwait(false);

        ReportConnectFailures(failures);
    }

    /// <summary>
    /// 将启动阶段连接失败的服务器汇总为一条 startup hint（软失败此前仅落日志，
    /// TUI 用户看不到，模型侧表现为"看不到该服务器的工具"）。
    /// </summary>
    private void ReportConnectFailures(ConcurrentBag<string> failures)
    {
        if (failures.IsEmpty || hintCollector is null)
            return;

        var detail = string.Join("；", failures.OrderBy(static f => f, StringComparer.Ordinal));
        var first = failures
            .Select(static f => f.Split(':', 2)[0].Trim())
            .OrderBy(static n => n, StringComparer.Ordinal)
            .First();

        hintCollector.Add(new StartupHint
        {
            Id = "mcp-connect-failures",
            Message = $"部分 MCP 服务器连接失败：{detail}。可用 /mcp connect <名称> 重试。",
            ActionCommand = $"/mcp connect {first}",
        });
    }

    private async Task ConnectOneWithTimeoutAsync(
        string name, McpServerDefinition def, CancellationToken ct, ConcurrentBag<string> failures)
    {
        var timeout = McpConnectionManager.ResolveConnectionTimeout(def);
        try
        {
            await connectionManager.ConnectOneAsync(name, def, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("MCP server '{Name}' connection timed out after {Timeout}", name, timeout);
            failures.Add($"{name}: 连接超时（>{timeout.TotalSeconds:0}s）");
            connectionManager.MarkConnectionError(name, $"连接超时（>{timeout.TotalSeconds:0}s）");
            return;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to connect to MCP server '{Name}'", name);
            failures.Add($"{name}: {ex.Message}");
            connectionManager.MarkConnectionError(name, ex.Message);
            return;
        }

        // ConnectOneAsyncCore 对传输层异常/超时软失败（记录断连状态、不抛出），
        // 这里按结果兜底判定，确保失败对用户可见。
        if (!connectionManager.IsConnected(name))
        {
            failures.Add(ct.IsCancellationRequested
                ? $"{name}: 连接超时（>{timeout.TotalSeconds:0}s）"
                : $"{name}: 连接失败（详见日志）");
            connectionManager.MarkConnectionError(name, ct.IsCancellationRequested
                ? $"连接超时（>{timeout.TotalSeconds:0}s）"
                : "连接失败（详见日志）");
        }
    }
}
