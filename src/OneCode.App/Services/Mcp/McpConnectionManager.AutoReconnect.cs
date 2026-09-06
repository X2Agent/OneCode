using ModelContextProtocol;
using OneCode.Infrastructure.Mcp;

namespace OneCode.App.Services.Mcp;

/// <summary>
/// MCP 服务器断连自动重连：对连接池中处于断连状态的条目（软失败落池的连接失败、
/// 运行中断连）按指数退避自动重连（上限 3 次，放弃后经 <see cref="IStartupHintCollector"/>
/// 提示手动 /mcp connect）。重连经 <c>ConnectOneAsync(name, def, ct)</c>——该重载会先清理
/// 同名陈旧条目，避免被 <c>ConnectOneAsyncCore</c> 的 ContainsKey 早退短路成 no-op。
/// 内置服务（playwright 等）是按需连接的正向约定——断开是常态，不自动拉起；
/// 用户显式 /mcp disconnect 的服务器同样跳过。
/// 运行中崩溃/挂起的服务器经每轮 MCP ping 探活发现（SDK 2.1.0 无断连事件，
/// 主动 ping 是唯一可靠的探活手段），失败即标记断连并由同轮循环重连。
/// </summary>
public sealed partial class McpConnectionManager
{
    /// <summary>自动重连检查间隔（internal 供测试注入加速循环；候选为空时循环空转无副作用）。</summary>
    internal static TimeSpan AutoReconnectInterval = TimeSpan.FromSeconds(60);

    /// <summary>自动重连最大尝试次数，超过后停止并提示用户手动恢复。</summary>
    private const int MaxAutoReconnectAttempts = 3;

    /// <summary>探活检出运行中断连的次数（诊断观察点；internal 供测试断言探活确实发生）。</summary>
    internal int RuntimeDisconnectDetections { get; private set; }

    /// <summary>重连尝试计数与上次尝试时间（指数退避节拍）。</summary>
    private readonly ConcurrentDictionary<string, (int Count, DateTimeOffset LastAttempt)> _reconnectAttempts = new();
    /// <summary>用户显式断开的服务器——自动重连不碰。</summary>
    private readonly ConcurrentDictionary<string, byte> _manuallyDisconnected = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _healthCts = new();
    private readonly Task? _healthLoopTask;

    /// <summary>单台服务器 ping 探活的超时上限（远程挂起服务器不应拖住整轮循环）。</summary>
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);

    private async Task AutoReconnectLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(AutoReconnectInterval, ct).ConfigureAwait(false);

                // 阶段 1：对已连接条目 ping 探活。运行中崩溃/挂起的服务器不会翻转条目的
                // IsConnected（SDK 无断连事件），唯有主动 ping 能发现；失败即标记断连，
                // 交由阶段 2 的重连候选处理。逐台串行，单台受 ProbeTimeout 约束。
                var connected = _connections
                    .Where(c => c.Value.IsConnected && !_manuallyDisconnected.ContainsKey(c.Key))
                    .ToList();
                foreach (var connEntry in connected)
                {
                    ct.ThrowIfCancellationRequested();
                    // 内置服务（playwright 等）按需连接是正向约定，探活失败不自动重拉。
                    if (BuiltInMcpServers.IsBuiltIn(connEntry.Key))
                        continue;
                    await ProbeConnectionAsync(connEntry, ct).ConfigureAwait(false);
                }

                // 阶段 2：重连断连条目（软失败落池 / 探活标记的运行中断连）。
                var candidates = _connections
                    .Where(c => !c.Value.IsConnected && !_manuallyDisconnected.ContainsKey(c.Key))
                    .ToList();
                if (candidates.Count == 0)
                    continue;

                var merged = await _multiScopeLoader.LoadAllAsync(ct: ct).ConfigureAwait(false);

                foreach (var (name, entry) in candidates)
                {
                    if (BuiltInMcpServers.IsBuiltIn(name))
                        continue;

                    // def 解析：配置优先（尊重禁用/删除）；配置缺失时回退池中定义——
                    // 注入式服务器（InProcess 测试/嵌入）不在 .mcp.json 落盘，同样需要重连。
                    if (!merged.Servers.TryGetValue(name, out var def))
                    {
                        if (entry.Definition.Disabled)
                            continue;
                        def = entry.Definition;
                    }
                    else if (def.Disabled)
                    {
                        continue;
                    }

                    var now = DateTimeOffset.UtcNow;
                    var state = _reconnectAttempts.GetOrAdd(name, _ => (0, DateTimeOffset.MinValue));

                    if (state.Count >= MaxAutoReconnectAttempts)
                    {
                        // 首次到达上限时上报 hint（用户可见），之后静默跳过。
                        if (state.Count == MaxAutoReconnectAttempts)
                        {
                            _reconnectAttempts[name] = (state.Count + 1, now);
                            _logger.LogError(
                                "MCP server '{Name}' auto-reconnect failed {Attempts} times — giving up. Use '/mcp connect' to recover manually.",
                                name, state.Count);
                            _hintCollector?.Add(new StartupHint
                            {
                                Id = $"mcp-auto-reconnect-{name}",
                                Message = $"MCP 服务器 '{name}' 已断开，自动重连 {MaxAutoReconnectAttempts} 次失败，已停止重试。",
                                ActionCommand = $"/mcp connect {name}",
                            });
                        }
                        continue;
                    }

                    var backoff = TimeSpan.FromSeconds(Math.Min(30 * Math.Pow(2, state.Count), 300));
                    if (now - state.LastAttempt < backoff)
                        continue;

                    _reconnectAttempts[name] = (state.Count + 1, now);
                    _logger.LogInformation(
                        "MCP server '{Name}' is disconnected — auto-reconnecting (attempt {Attempt}/{Max})",
                        name, state.Count + 1, MaxAutoReconnectAttempts);
                    await ConnectOneAsync(name, def, ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "MCP auto-reconnect loop error");
            }
        }
    }

    /// <summary>
    /// 对单个已连接条目做 MCP ping 探活。失败时把条目标记为断连（带原因），
    /// 由同轮重连候选接管。返回条目是否仍然健康。
    /// </summary>
    private async Task<bool> ProbeConnectionAsync(KeyValuePair<string, McpServerConnection> connEntry, CancellationToken ct)
    {
        var (name, entry) = (connEntry.Key, connEntry.Value);
        var sdk = entry.Client.SdkClient;
        if (sdk is null)
            return true;

        using var window = CancellationTokenSource.CreateLinkedTokenSource(ct);
        window.CancelAfter(ProbeTimeout);
        try
        {
            // SDK 2.1.0 签名：PingAsync(RequestOptions, CancellationToken)。
            // 注意：对端死亡时 PingAsync 在"等待响应"阶段不响应取消令牌（请求写入
            // 成功后即挂起），外层 WaitAsync 兜底保证探活窗口刚性生效——挂起的
            // ping 任务一次性泄漏可接受（条目标记断连后不再探活；重连是新 client 实例）。
            await sdk.PingAsync(new RequestOptions(), window.Token)
                .AsTask()
                .WaitAsync(window.Token)
                .ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            MarkRuntimeDisconnected(name, $"ping 探活超时（>{ProbeTimeout.TotalSeconds:0}s）");
            return false;
        }
        catch (ModelContextProtocol.McpException)
        {
            // 远端协议错误响应（如 SDK 2.1.0 server 对 2026-07-28 协议不内置 ping 处理器的
            // method-not-found）恰恰证明链路双向可达——server 活着。判定健康，不标记断连。
            return true;
        }
        catch (Exception ex)
        {
            MarkRuntimeDisconnected(name, $"ping 探活失败：{ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 把运行中断连写回连接池条目（状态栏三态 / /mcp list 可见）。仅在条目仍是
    /// 已连接状态时翻转——并发重连可能已重建条目，此时忽略本次探活结果，避免误标新连接。
    /// </summary>
    private void MarkRuntimeDisconnected(string name, string reason)
    {
        RuntimeDisconnectDetections++;
        if (_connections.TryGetValue(name, out var entry) && entry.IsConnected)
            _connections[name] = entry with { IsConnected = false, LastError = reason };
        _logger.LogWarning("MCP server '{Name}' runtime liveness check failed: {Reason}", name, reason);
    }
}
