using Microsoft.Agents.AI.Mcp;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using OneCode.Core.Mcp;
using OneCode.Infrastructure.Mcp;

namespace OneCode.App.Services.Mcp;

/// <summary>
/// Manages MCP server connection lifecycle and exposes their tools to QueryEngine.
/// Config is loaded from multi-scope .mcp.json files (user, project, local) via
/// <see cref="McpMultiScopeConfigLoader"/>.
/// </summary>
public sealed partial class McpConnectionManager : IMcpConnectionManager
{
    private readonly ILogger<McpConnectionManager> _logger;
    private readonly ILoggerFactory _clientLoggerFactory;
    private readonly McpMultiScopeConfigLoader _multiScopeLoader;
    private readonly McpElicitationHandler _elicitationHandler;
    private readonly IStartupHintCollector? _hintCollector;
    private readonly InProcessMcpServerRegistry? _inProcessRegistry;
    private readonly ConcurrentDictionary<string, McpServerConnection> _connections = new(StringComparer.Ordinal);
    // 同名服务器的连接门闩：将"检查-连接-写入"串行化，避免并发调用下
    // 非原子的 ContainsKey 检查导致重复创建客户端或互相断开对方刚建立的连接。
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _connectGates = new(StringComparer.Ordinal);
    // 正在握手（尚未定论成败）的连接数——状态栏三态的"连接中"数据源。
    private readonly ConcurrentDictionary<string, byte> _inFlight = new(StringComparer.Ordinal);
    // 最近一次启动连接的启用服务器目标数（状态栏"连接中 x/y"的分母）。
    private volatile int _expectedCount;
    private bool _disposed;

    public McpConnectionManager(
        McpMultiScopeConfigLoader multiScopeLoader,
        McpElicitationHandler elicitationHandler,
        IStartupHintCollector? hintCollector = null,
        InProcessMcpServerRegistry? inProcessRegistry = null,
        ILogger<McpConnectionManager>? logger = null,
        ILoggerFactory? loggerFactory = null)
    {
        _logger = logger ?? NullLogger<McpConnectionManager>.Instance;
        // 底层 McpClient 的日志工厂：此前固定 NullLogger 导致连接期（SDK 握手/传输层）
        // 日志全部静默，连接失败只能靠异常消息冒泡，排障信息缺失。
        _clientLoggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        _multiScopeLoader = multiScopeLoader;
        _elicitationHandler = elicitationHandler;
        _hintCollector = hintCollector;
        _inProcessRegistry = inProcessRegistry;
        _healthLoopTask = Task.Run(() => AutoReconnectLoopAsync(_healthCts.Token));
    }

    // Events

    /// <summary>
    /// Fires whenever the set of connected MCP servers changes (connect or disconnect).
    /// Subscribers should re-run <see cref="Commands.McpCommandSource.LoadCommandsAsync"/> to
    /// pick up new or removed /mcp:{server} slash commands.
    /// </summary>
    public event Action? ServersChanged;

    // Connect / Disconnect

    /// <summary>Default timeout per MCP server connection (30 seconds).</summary>
    private static readonly TimeSpan ConnectionTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// 整个启动连接阶段的总预算（60 秒）。每台服务器各自享有 <see cref="ConnectionTimeout"/>
    /// 独立窗口（避免一台慢服务器吃掉全局超时），总体仍受该预算约束防止启动被无限拖住。
    /// </summary>
    private static readonly TimeSpan StartupBudgetTimeout = TimeSpan.FromSeconds(60);

    public async Task ConnectAllAsync(CancellationToken ct = default)
    {
        var merged = await _multiScopeLoader.LoadAllAsync(ct: ct).ConfigureAwait(false);
        var enabled = merged.Servers.Where(kv => !kv.Value.Disabled).ToDictionary(kv => kv.Key, kv => kv.Value);
        var disabledCount = merged.Servers.Count(kv => kv.Value.Disabled);

        if (disabledCount > 0)
            _logger.LogInformation("Skipping {Count} disabled MCP server(s).", disabledCount);

        // 内置服务（如 playwright）默认按需连接：不随启动连接，由消费方
        // 在首次使用时通过 ConnectOneAsync 触发。连接时机是"正向约定"，
        // 无需用户在配置里表达。
        var builtIn = enabled.Where(kv => BuiltInMcpServers.IsBuiltIn(kv.Key)).Select(kv => kv.Key).ToList();
        if (builtIn.Count > 0)
        {
            _logger.LogDebug("Deferring {Count} built-in MCP server(s) (on-demand): {Names}", builtIn.Count, string.Join(", ", builtIn));
            enabled = enabled.Where(kv => !BuiltInMcpServers.IsBuiltIn(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value);
        }

        if (enabled.Count == 0)
        {
            _logger.LogDebug("No MCP servers to connect at startup.");
            _expectedCount = 0;
            return;
        }

        // 记录目标数（状态栏三态"连接中 x/y"的分母）。内置按需服务已被剔除，不计入。
        _expectedCount = enabled.Count;
        _logger.LogInformation("Connecting to {Count} MCP server(s)...", enabled.Count);

        using var startupBudget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        startupBudget.CancelAfter(StartupBudgetTimeout);

        // 并发连接（与既有行为一致），但每台服务器持有独立的超时窗口（配置
        // initTimeoutMs / startupTimeoutMs 可覆盖，未配置回落 30s），链接到启动总预算：
        // 任一服务器超时不影响其他服务器的连接进度。
        var failures = new ConcurrentBag<string>();
        await Task.WhenAll(enabled.Select(async kv =>
        {
            using var perServer = CancellationTokenSource.CreateLinkedTokenSource(ct, startupBudget.Token);
            perServer.CancelAfter(ResolveConnectionTimeout(kv.Value));
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
        if (failures.IsEmpty || _hintCollector is null)
            return;

        var detail = string.Join("；", failures.OrderBy(static f => f, StringComparer.Ordinal));
        var first = failures
            .Select(static f => f.Split(':', 2)[0].Trim())
            .OrderBy(static n => n, StringComparer.Ordinal)
            .First();

        _hintCollector.Add(new StartupHint
        {
            Id = "mcp-connect-failures",
            Message = $"部分 MCP 服务器连接失败：{detail}。可用 /mcp connect <名称> 重试。",
            ActionCommand = $"/mcp connect {first}",
        });
    }

    private async Task ConnectOneWithTimeoutAsync(
        string name, McpServerDefinition def, CancellationToken ct, ConcurrentBag<string> failures)
    {
        var timeout = ResolveConnectionTimeout(def);
        try
        {
            await ConnectOneAsync(name, def, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("MCP server '{Name}' connection timed out after {Timeout}", name, timeout);
            failures.Add($"{name}: 连接超时（>{timeout.TotalSeconds:0}s）");
            MarkConnectionError(name, $"连接超时（>{timeout.TotalSeconds:0}s）");
            return;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to connect to MCP server '{Name}'", name);
            failures.Add($"{name}: {ex.Message}");
            MarkConnectionError(name, ex.Message);
            return;
        }

        // ConnectOneAsyncCore 对传输层异常/超时软失败（记录断连状态、不抛出），
        // 这里按结果兜底判定，确保失败对用户可见。
        if (!_connections.TryGetValue(name, out var conn) || !conn.IsConnected)
        {
            failures.Add(ct.IsCancellationRequested
                ? $"{name}: 连接超时（>{timeout.TotalSeconds:0}s）"
                : $"{name}: 连接失败（详见日志）");
            MarkConnectionError(name, ct.IsCancellationRequested
                ? $"连接超时（>{timeout.TotalSeconds:0}s）"
                : "连接失败（详见日志）");
        }
    }

    /// <summary>把失败原因写进连接池条目（<see cref="McpServerStatus.LastError"/>）——/mcp list 的失败详情数据源。条目尚不存在（如门闩等待期间被取消）时为空操作。</summary>
    private void MarkConnectionError(string name, string error)
    {
        if (_connections.TryGetValue(name, out var conn) && !conn.IsConnected)
            _connections[name] = conn with { LastError = error };
    }

    /// <summary>
    /// Hot-connect a single server by name, loading its definition from the
    /// multi-scope config files. Returns false if the name is not configured or
    /// the connection failed. Safe to call repeatedly (reconnects).
    /// </summary>
    public async Task<bool> ConnectOneAsync(string name, CancellationToken ct = default)
    {
        var merged = await _multiScopeLoader.LoadAllAsync(ct: ct).ConfigureAwait(false);

        if (!merged.Servers.TryGetValue(name, out var def))
            return false;

        if (def.Disabled)
        {
            _logger.LogInformation("MCP server '{Name}' is disabled — skipping connection.", name);
            return false;
        }

        // 热连接（/mcp connect、按需连接）与启动路径同享每服务器超时窗口，
        // 防止慢服务器把调用方（命令/工具）无限挂起。
        using var window = CancellationTokenSource.CreateLinkedTokenSource(ct);
        window.CancelAfter(ResolveConnectionTimeout(def));

        var gate = await AcquireConnectGateAsync(name, window.Token).ConfigureAwait(false);
        try
        {
            // Drop any stale connection first so the reconnect below
            // isn't short-circuited by the "already connected" early-return in the core.
            if (_connections.TryRemove(name, out var prior))
            {
                try { await prior.Client.DisposeAsync().ConfigureAwait(false); }
                catch (Exception ex) { _logger.LogWarning(ex, "Error disposing prior MCP client for '{Name}'", name); }
            }

            await ConnectOneAsyncCore(name, def, window.Token).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }

        return _connections.TryGetValue(name, out var updated) && updated.IsConnected;
    }

    public async Task ConnectOneAsync(string name, McpServerDefinition def, CancellationToken ct = default)
    {
        var gate = await AcquireConnectGateAsync(name, ct).ConfigureAwait(false);
        try
        {
            // 先清理同名的软失败/断连陈旧条目，否则下面的 ConnectOneAsyncCore 会被
            // 其 ContainsKey 早退短路成 no-op（AutoReconnect 循环正是经本重载重连的）。
            // 已连接的条目不动——本重载必须保持幂等，重复调用不得拆掉活跃连接。
            if (_connections.TryGetValue(name, out var existing) && !existing.IsConnected
                && _connections.TryRemove(name, out var stale))
            {
                try { await stale.Client.DisposeAsync().ConfigureAwait(false); }
                catch (Exception ex) { _logger.LogWarning(ex, "Error disposing stale MCP client for '{Name}'", name); }
            }

            await ConnectOneAsyncCore(name, def, ct).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// 获取指定服务器的连接门闩，并将连接操作串行化。同名服务器的并发连接/重连
    /// （如按需连接、/mcp connect、ConnectAll）不会同时越过 <see cref="ConnectOneAsyncCore"/>
    /// 里非原子的 ContainsKey 检查，避免重复创建客户端或互相断开对方刚建立的连接。
    /// 门闩按名缓存；连接集合很小，会话生命周期内增长有界。
    /// </summary>
    private async Task<SemaphoreSlim> AcquireConnectGateAsync(string name, CancellationToken ct)
    {
        var gate = _connectGates.GetOrAdd(name, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        return gate;
    }

    /// <summary>
    /// 解析单台服务器的连接超时窗口：配置 <c>initTimeoutMs</c>/<c>startupTimeoutMs</c>
    /// 优先生效（startupTimeoutMs 历史上承载 stdio 子进程拉起时长，取两者较大值），未配置回落
    /// <see cref="ConnectionTimeout"/>（30s）。非法值（≤0）同样回落默认，避免配置错误导致永不超时。
    /// </summary>
    private static TimeSpan ResolveConnectionTimeout(McpServerDefinition def)
    {
        var fromConfig = Math.Max(def.InitTimeoutMs ?? 0, def.StartupTimeoutMs ?? 0);
        return fromConfig > 0 ? TimeSpan.FromMilliseconds(fromConfig) : ConnectionTimeout;
    }

    /// <summary>
    /// 在已持有 <see cref="_connectGates"/> 门闩的前提下执行实际连接。
    /// 调用方负责获取/释放门闩；本方法不做加锁，避免重复获取造成死锁。
    /// </summary>
    private async Task ConnectOneAsyncCore(string name, McpServerDefinition def, CancellationToken ct)
    {
        if (_connections.ContainsKey(name))
            return;

        // 连接前校验配置有效性，避免无效定义（如 http/sse/ws 缺 Url、stdio 缺 Command）
        // 在 def.Url! / def.Command ?? "" 处触发 NRE 或静默失败。
        if (!def.IsValid)
        {
            _logger.LogWarning(
                "MCP server '{Name}' has invalid configuration (transport '{Type}' requires Command for stdio / Url for http/sse/ws) -- skipping",
                name, def.TransportType);
            return;
        }

        var client = new McpClient(_clientLoggerFactory.CreateLogger<McpClient>(), _elicitationHandler);
        var conn = new McpServerConnection(name, def, client);

        _inFlight[name] = 1;
        string? error = null;
        try
        {
            switch (def.TransportType)
            {
                case McpTransportType.Stdio:
                    await client.ConnectStdioAsync(def.Command ?? "", def.Args ?? [], def.Env, ct);
                    var stdioTools = await LoadAgentToolsAsync(client, name, def, ct).ConfigureAwait(false);
                    conn = conn with { AgentTools = stdioTools, IsConnected = true };
                    _logger.LogInformation("MCP server '{Name}' connected ({Count} tools)", name, stdioTools.Count);
                    break;

                case McpTransportType.Sse:
                    await client.ConnectSseAsync(def.Url!, def.Headers, ct);
                    var sseTools = await LoadAgentToolsAsync(client, name, def, ct).ConfigureAwait(false);
                    conn = conn with { AgentTools = sseTools, IsConnected = true };
                    _logger.LogInformation("MCP server '{Name}' connected via SSE ({Count} tools)", name, sseTools.Count);
                    break;

                case McpTransportType.Http:
                    await client.ConnectHttpAsync(def.Url!, def.Headers, ct);
                    var httpTools = await LoadAgentToolsAsync(client, name, def, ct).ConfigureAwait(false);
                    conn = conn with { AgentTools = httpTools, IsConnected = true };
                    _logger.LogInformation("MCP server '{Name}' connected via HTTP ({Count} tools)", name, httpTools.Count);
                    break;

                case McpTransportType.WebSocket:
                    if (string.IsNullOrEmpty(def.Url))
                    {
                        _logger.LogWarning("MCP WebSocket transport requires a URL -- skipping '{Name}'", name);
                        break;
                    }
                    var wsTransport = new WebSocketClientTransport(def.Url, _logger);
                    await client.ConnectAsync(wsTransport, ct);
                    var wsTools = await LoadAgentToolsAsync(client, name, def, ct).ConfigureAwait(false);
                    conn = conn with { AgentTools = wsTools, IsConnected = true };
                    _logger.LogInformation("MCP server '{Name}' connected via WebSocket ({Count} tools)", name, wsTools.Count);
                    break;

                case McpTransportType.InProcess:
                    // 同进程服务器：注册表按名解析内存 transport 对，provider 在 server 侧
                    // 启动 McpServer 会话；未注册时保持软失败（跳过 + 警告），不中断其他服务器。
                    var inProcessPair = _inProcessRegistry?.TryCreatePair(name);
                    if (inProcessPair is null)
                    {
                        _logger.LogWarning(
                            "MCP in-process server '{Name}' has no registered provider -- skipping", name);
                        break;
                    }
                    await client.ConnectAsync(inProcessPair.Client, ct);
                    var inProcessTools = await LoadAgentToolsAsync(client, name, def, ct).ConfigureAwait(false);
                    conn = conn with { AgentTools = inProcessTools, IsConnected = true };
                    _logger.LogInformation("MCP server '{Name}' connected in-process ({Count} tools)", name, inProcessTools.Count);
                    break;

                default:
                    _logger.LogWarning("MCP server '{Name}' unrecognized transport type '{Type}' -- skipping", name, def.TransportType);
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            // 超时/取消必须传播：外层 ConnectOneWithTimeoutAsync 依赖它区分
            // "连接超时" 与 "连接失败"，调用方依赖它感知用户取消。
            // inFlight 在 finally 清理，连接池不落条目（与既有行为一致）。
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to connect MCP server '{Name}'", name);
            error = ex.Message;
        }
        finally
        {
            _inFlight.TryRemove(name, out _);
        }

        // 失败同样落池（软失败语义）：连接集合变化统一广播——状态栏三态与
        // /mcp list 依赖此事件刷新，否则一台失败的服务器会让指示停留在"连接中"。
        _connections[name] = conn with { LastError = conn.IsConnected ? null : error };
        if (conn.IsConnected)
        {
            // 连接成功即清除断开状态标记与自动重连计数。
            _manuallyDisconnected.TryRemove(name, out _);
            _reconnectAttempts.TryRemove(name, out _);
        }
        ServersChanged?.Invoke();
    }

    public async Task DisconnectAsync(string name)
    {
        if (_connections.TryRemove(name, out var conn))
        {
            // 显式断开：自动重连不再拉起，直到下次显式连接成功。
            _manuallyDisconnected[name] = 1;
            await conn.Client.DisposeAsync();
            ServersChanged?.Invoke();
        }
    }

    public async Task ReconnectServerAsync(string name, CancellationToken ct = default)
    {
        // 与并发 ConnectOneAsync 走同一门闩，避免重连与按需连接互相拆对方的连接；
        // 门闩内先摘除旧连接并广播变化，再按配置重连。
        var gate = await AcquireConnectGateAsync(name, ct).ConfigureAwait(false);
        try
        {
            if (_connections.TryRemove(name, out var existing))
            {
                await existing.Client.DisposeAsync();
                ServersChanged?.Invoke();
            }

            var merged = await _multiScopeLoader.LoadAllAsync(ct: ct).ConfigureAwait(false);

            if (!merged.Servers.TryGetValue(name, out var def) || def.Disabled)
            {
                _logger.LogInformation("MCP server '{Name}' is not configured or disabled — skipping reconnection.", name);
                return;
            }

            using var window = CancellationTokenSource.CreateLinkedTokenSource(ct);
            window.CancelAfter(ResolveConnectionTimeout(def));
            await ConnectOneAsyncCore(name, def, window.Token);
        }
        finally
        {
            gate.Release();
        }
    }

    public IReadOnlyList<string> GetServerNames()
        => _connections.Keys.ToList();

    /// <summary>已连接且可用的 MCP 客户端（供技能/工具 MAF 集成使用）。</summary>
    public IReadOnlyList<(string Name, IMcpClient Client)> GetConnectedClients()
        => _connections.Values
            .Where(c => c.IsConnected)
            .Select(c => (c.Name, (IMcpClient)c.Client))
            .ToList();

    /// <summary>Lookup a connected client by server name (null if not connected).</summary>
    public IMcpClient? GetClient(string name)
        => _connections.TryGetValue(name, out var c) && c.IsConnected ? c.Client : null;

    // Tools

    /// <summary>
    /// Get all live tools from connected MCP servers as <see cref="AIFunction"/> adapters.
    /// Safe to call multiple times; returns a fresh snapshot each time.
    /// </summary>
    public IReadOnlyList<AIFunction> GetAllTools()
        => _connections.Values
            .Where(c => c.IsConnected)
            .SelectMany(c => c.AgentTools)
            .ToList();

    public IReadOnlyList<McpServerStatus> GetStatus()
        => _connections.Values
            .Select(c => new McpServerStatus(c.Name, c.Definition, c.IsConnected, c.AgentTools.Count, c.LastError))
            .ToList();

    /// <summary>
    /// 连接池三态快照：Expected 来自最近一次 <see cref="ConnectAllAsync"/> 的启用服务器数，
    /// Connecting 为正在握手的连接数，Failed 为池中断连且不在握手中的条目（软失败）。
    /// </summary>
    public McpConnectionSummary GetConnectionSummary()
    {
        var connected = 0;
        var toolCount = 0;
        var failed = 0;
        foreach (var c in _connections.Values)
        {
            if (c.IsConnected)
            {
                connected++;
                toolCount += c.AgentTools.Count;
            }
            else if (!_inFlight.ContainsKey(c.Name))
            {
                failed++;
            }
        }

        return new McpConnectionSummary(_expectedCount, connected, _inFlight.Count, failed, toolCount);
    }

    // Helpers

    private const int MaxFunctionNameLength = 64;

    /// <summary>
    /// 用 MAF 桥接（Microsoft.Agents.AI.Mcp 的
    /// <see cref="McpClientTaskExtensions.ListAgentToolsWithTasksAsync"/>）加载工具：
    /// 透明处理 MCP 2026-07-28 Tasks extension（long-running 工具轮询、input_required 解析、
    /// 远程取消），同时兼容不支持 task 的普通同步工具。
    /// 工具名经 <see cref="RenamedAIFunction"/> 包装为 <c>mcp__{server}__{tool}</c> 前缀
    /// （MAF 包装器不暴露 WithName）。
    /// </summary>
    private static async Task<IReadOnlyList<AIFunction>> LoadAgentToolsAsync(
        OneCode.Infrastructure.Mcp.McpClient client,
        string serverName,
        McpServerDefinition def,
        CancellationToken ct)
    {
        var sdk = client.SdkClient
            ?? throw new InvalidOperationException($"MCP server '{serverName}' is not connected.");
        var tools = await sdk.ListAgentToolsWithTasksAsync(cancellationToken: ct).ConfigureAwait(false);
        if (tools.Count == 0)
            return [];

        // 工具白名单过滤（McpToolFilter）：只把配置勾选的方法包装进 AgentTools。
        // 过滤发生在改名/包装之前，未选中的工具不产生任何 AIFunction 开销；
        // GetAllTools/GetStatus/ToolCatalog 因此天然只暴露选中的方法。
        var prefixed = new List<AIFunction>(tools.Count);
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var tool in tools)
        {
            if (!McpToolFilter.IsEnabled(def, tool.Name))
                continue;

            prefixed.Add(new RenamedAIFunction(tool, CreateUniqueToolName(serverName, tool.Name, usedNames)));
        }

        return prefixed;
    }

    /// <summary>
    /// Reload the agent tools of an already-connected server (hot-apply the tool whitelist
    /// after selection changes) without dropping the underlying connection. The connection
    /// entry keeps its identity; only <see cref="McpServerConnection.AgentTools"/> is replaced.
    /// </summary>
    public async Task<bool> ReloadToolsAsync(string name, CancellationToken ct = default)
    {
        if (!_connections.TryGetValue(name, out var conn) || !conn.IsConnected)
            return false;

        // 优先从多作用域配置重读定义（白名单可能刚被 /mcp enable-tool 或配置页写回）；
        // 配置文件中不存在（如内置服务）时沿用连接时的定义。
        var merged = await _multiScopeLoader.LoadAllAsync(ct: ct).ConfigureAwait(false);
        var def = merged.Servers.TryGetValue(name, out var fromConfig) ? fromConfig : conn.Definition;

        // Reload on the live client — no reconnect, no DisposeAsync, no transport churn.
        var reloaded = await LoadAgentToolsAsync(conn.Client, name, def, ct).ConfigureAwait(false);
        var updated = conn with { AgentTools = reloaded };
        _connections[name] = updated;

        if (reloaded.Count != conn.AgentTools.Count)
        {
            _logger.LogInformation(
                "MCP server '{Name}' tool whitelist applied: {Before} -> {After} tools",
                name, conn.AgentTools.Count, reloaded.Count);
        }

        ServersChanged?.Invoke();
        return true;
    }

    internal static string CreateUniqueToolName(
        string serverName, string toolName, ISet<string> usedNames)
    {
        ArgumentNullException.ThrowIfNull(usedNames);
        var server = NormalizeNameSegment(serverName, "server");
        var tool = NormalizeNameSegment(toolName, "tool");
        var prefix = $"mcp__{server}__";
        var available = Math.Max(1, MaxFunctionNameLength - prefix.Length);
        var baseName = prefix + tool[..Math.Min(tool.Length, available)];
        if (usedNames.Add(baseName)) return baseName;

        for (var suffix = 2; ; suffix++)
        {
            var suffixText = $"__{suffix}";
            var bodyLength = Math.Max(1, MaxFunctionNameLength - prefix.Length - suffixText.Length);
            var candidate = prefix + tool[..Math.Min(tool.Length, bodyLength)] + suffixText;
            if (usedNames.Add(candidate)) return candidate;
        }
    }

    private static string NormalizeNameSegment(string? value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        var chars = value.Select(static c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-'
                ? c
                : '_')
            .ToArray();
        var result = new string(chars).Trim('_');
        return string.IsNullOrEmpty(result) ? fallback : result;
    }

    // IAsyncDisposable

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        _healthCts.Cancel();
        if (_healthLoopTask != null)
        {
            try
            {
                await _healthLoopTask.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Auto-reconnect loop did not complete within timeout");
            }
        }
        _healthCts.Dispose();

        // 逐个清理并吞掉单个客户端的释放异常——一个失败的 Dispose
        // 不应中断其余连接的清理，也不应让 DisposeAsync 本身抛异常。
        foreach (var conn in _connections.Values)
        {
            try
            {
                await conn.Client.DisposeAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error disposing MCP client for '{Name}'", conn.Name);
            }
        }
        _connections.Clear();
    }
}

// Supporting types

internal sealed record McpServerConnection(
    string Name,
    McpServerDefinition Definition,
    McpClient Client,
    bool IsConnected = false,
    IReadOnlyList<AIFunction>? AgentTools = null,
    string? LastError = null)
{
    public IReadOnlyList<AIFunction> AgentTools { get; init; } = AgentTools ?? [];
}

/// <summary>
/// Wraps an <see cref="AIFunction"/> to override its <see cref="Name"/> with a server-prefixed name.
/// Used for MAF <c>TaskAwareMcpClientAIFunction</c> instances that don't expose <c>WithName</c>.
/// </summary>
internal sealed class RenamedAIFunction : AIFunction
{
    private readonly AIFunction _inner;
    private readonly string _name;

    public RenamedAIFunction(AIFunction inner, string name)
    {
        _inner = inner;
        _name = name;
    }

    public override string Name => _name;
    public override string Description => _inner.Description;
    public override JsonElement JsonSchema => _inner.JsonSchema;
    public override JsonElement? ReturnJsonSchema => _inner.ReturnJsonSchema;
    public override JsonSerializerOptions JsonSerializerOptions => _inner.JsonSerializerOptions;

    protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
        => _inner.InvokeAsync(arguments, cancellationToken);
}


