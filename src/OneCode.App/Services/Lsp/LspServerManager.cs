using OneCode.Core.Lsp;
using OneCode.Infrastructure.Config;

namespace OneCode.App.Services.Lsp;

/// <summary>
/// Manages multiple LSP server instances — lifecycle, health checks, and diagnostic collection.
/// 崩溃自愈对齐 MCP 侧语义：指数退避 + 上限放弃 + 成功清零计数（<see cref="_restartAttempts"/>），
/// 放弃后仍可经 <see cref="RestartServerAsync"/>（/lsp restart）手动恢复。
/// </summary>
public sealed class LspServerManager : ILspServerManager, IAsyncDisposable
{
    private readonly ILogger<LspServerManager> _logger;
    private readonly LspDiagnosticRegistry _diagnosticRegistry;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ConcurrentDictionary<string, LspServerInstance> _servers = new();
    private readonly CancellationTokenSource _healthCheckCts = new();
    private readonly Task? _healthCheckTask;
    private bool _disposed;

    /// <summary>崩溃自动重启计数与上次尝试时间（指数退避节拍）。字段级生命周期：实例重建不丢，恢复健康/手动重启即清零。</summary>
    private readonly ConcurrentDictionary<string, (int Count, DateTimeOffset LastAttempt)> _restartAttempts = new(StringComparer.Ordinal);
    /// <summary>最近一次启动失败原因（软失败落池：StartServerAsync 失败不保留实例，错误单独记录供 GetStatus 映射）。</summary>
    private readonly ConcurrentDictionary<string, string> _startErrors = new(StringComparer.Ordinal);

    public LspServerManager(ILogger<LspServerManager> logger, LspDiagnosticRegistry diagnosticRegistry, ILoggerFactory loggerFactory)
    {
        _logger = logger;
        _diagnosticRegistry = diagnosticRegistry;
        _loggerFactory = loggerFactory;
        _healthCheckTask = Task.Run(() => HealthCheckLoopAsync(_healthCheckCts.Token));
    }

    /// <summary>
    /// Start an LSP server with the given configuration.
    /// </summary>
    public async Task<bool> StartServerAsync(LspServerConfig config, CancellationToken ct = default)
    {
        if (_servers.ContainsKey(config.Name))
        {
            _logger.LogWarning("LSP server {ServerName} already running", config.Name);
            return false;
        }

        var instance = new LspServerInstance(config, _loggerFactory, _diagnosticRegistry);
        _servers[config.Name] = instance;

        try
        {
            await instance.StartAsync().ConfigureAwait(false);
            _logger.LogInformation("LSP server {ServerName} started successfully", config.Name);
            // 启动成功：清零崩溃重启计数与历史启动错误（对齐 MCP 连接成功清零语义）。
            _restartAttempts.TryRemove(config.Name, out _);
            _startErrors.TryRemove(config.Name, out _);
            return true;
        }
        catch (Exception ex)
        {
            _servers.TryRemove(config.Name, out _);
            _startErrors[config.Name] = ex.Message;
            // Kill the orphaned process — StartAsync may have launched the server before
            // initialize timed out / failed, leaving a zombie csharp-ls otherwise.
            try
            {
                await instance.StopAsync().ConfigureAwait(false);
            }
            catch (Exception stopEx)
            {
                _logger.LogDebug(stopEx, "Failed to stop LSP server {ServerName} after start failure", config.Name);
            }
            _logger.LogError(ex, "Failed to start LSP server {ServerName}", config.Name);
            return false;
        }
    }

    /// <summary>
    /// Stop an LSP server by name.
    /// </summary>
    public async Task<bool> StopServerAsync(string name, CancellationToken ct = default)
    {
        if (!_servers.TryRemove(name, out var instance))
        {
            _logger.LogWarning("LSP server {ServerName} not found", name);
            return false;
        }

        try
        {
            await instance.StopAsync().ConfigureAwait(false);
            _logger.LogInformation("LSP server {ServerName} stopped", name);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error stopping LSP server {ServerName}", name);
            return false;
        }
    }

    /// <inheritdoc cref="ILspServerManager.RestartServerAsync"/>
    public async Task<bool> RestartServerAsync(string name, CancellationToken ct = default)
    {
        if (!_servers.TryRemove(name, out var instance))
        {
            _logger.LogWarning("LSP server {ServerName} not running — nothing to restart (use /lsp enable)", name);
            return false;
        }

        // stop 失败不阻断重启：旧实例已出池，新实例与旧进程互不影响。
        try
        {
            await instance.StopAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to stop LSP server {ServerName} before restart", name);
        }

        var restarted = await StartServerAsync(instance.Config, ct).ConfigureAwait(false);
        if (restarted)
            _logger.LogInformation("LSP server {ServerName} restarted", name);
        return restarted;
    }

    /// <summary>
    /// Send a request to an LSP server.
    /// </summary>
    public async Task<JsonElement?> SendRequestAsync(string serverName, string method, JsonElement parameters, CancellationToken ct = default)
    {
        if (!_servers.TryGetValue(serverName, out var instance))
            throw new KeyNotFoundException($"LSP server {serverName} not found");

        return await instance.SendRequestAsync(method, parameters, ct).ConfigureAwait(false);
    }

    public async Task SendNotificationAsync(string serverName, string method, JsonElement parameters)
    {
        if (!_servers.TryGetValue(serverName, out var instance))
            throw new KeyNotFoundException($"LSP server {serverName} not found");

        await SendWithTimeoutAsync(
            instance.SendNotificationAsync(method, parameters),
            Constants.Lsp.NotificationSendTimeout,
            instance.MarkUnhealthy,
            ex => _logger.LogDebug(ex, "Abandoned LSP send to {Name} faulted after timeout", serverName)).ConfigureAwait(false);
    }

    public async Task BroadcastNotificationAsync(string method, JsonElement parameters)
    {
        foreach (var instance in _servers.Values)
        {
            try
            {
                if (instance.IsInitialized)
                {
                    await SendWithTimeoutAsync(
                        instance.SendNotificationAsync(method, parameters),
                        Constants.Lsp.NotificationSendTimeout,
                        instance.MarkUnhealthy,
                        ex => _logger.LogDebug(ex, "Abandoned LSP send to {Name} faulted after timeout", instance.Config.Name)).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to send notification to LSP server {Name}", instance.Config.Name);
            }
        }
    }

    /// <summary>
    /// 通知发送的统一等待入口。stdin 管道直写（LspProtocol.WriteFrameAsync）无内建超时，
    /// 服务器假死（活着但不读 stdin、缓冲区写满）会让 WriteAsync 永久阻塞——而广播在
    /// Write/Edit/Delete 完成路径上同步 await，一处挂起即拖死全部文件工具。超时即回调
    /// onTimeout 标记 unhealthy，交由健康检查循环按崩溃自愈策略接管。
    /// </summary>
    private Task SendWithTimeoutAsync(LspServerInstance instance, Task sendTask) =>
        SendWithTimeoutAsync(
            sendTask,
            Constants.Lsp.NotificationSendTimeout,
            instance.MarkUnhealthy,
            ex => _logger.LogDebug(ex, "Abandoned LSP send to {Name} faulted after timeout", instance.Config.Name));

    /// <summary>internal 供单测注入短超时与伪挂起任务。</summary>
    internal static async Task SendWithTimeoutAsync(
        Task sendTask, TimeSpan timeout, Action<Exception> onTimeout, Action<Exception>? observeAbandonedFault)
    {
        var completed = await Task.WhenAny(sendTask, Task.Delay(timeout)).ConfigureAwait(false);
        if (completed != sendTask)
        {
            // 被抛弃的发送任务最终完成时其异常必须被观察（防 UnobservedTaskException）。
            _ = sendTask.ContinueWith(
                t => observeAbandonedFault?.Invoke(t.Exception!),
                TaskContinuationOptions.OnlyOnFaulted);

            var error = new TimeoutException(
                $"LSP notification send timed out after {timeout.TotalSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture)}s — stdin pipe stalled");
            onTimeout(error);
            throw error;
        }

        // 正常完成：观察/传播底层异常（Broadcast 调用方 catch 记录；单发调用方按需处理）。
        await sendTask.ConfigureAwait(false);
    }

    /// <summary>
    /// Get diagnostic information for a server.
    /// Delegates to LspDiagnosticRegistry — the single source of truth for diagnostics.
    /// Converts App-layer LspDiagnostic to Core-layer LspDiagnosticEntry for the public interface.
    /// </summary>
    public IReadOnlyList<LspDiagnosticEntry> GetDiagnostics(string? serverName = null)
    {
        var diagnostics = serverName != null
            ? _diagnosticRegistry.GetDiagnostics(serverName)
            : _diagnosticRegistry.GetAllDiagnostics();

        return diagnostics.Select(d => new LspDiagnosticEntry
        {
            ServerName = d.ServerName,
            Severity = d.Severity,
            Message = d.Message,
            Timestamp = d.Timestamp,
            File = d.FilePath,
            Line = d.Range.StartLine + 1,  // LSP uses 0-based lines; convert to 1-based
            Column = d.Range.StartColumn + 1
        }).ToList();
    }

    /// <summary>
    /// Get status of all servers. 含启动失败（软失败落池）的服务器条目，失败原因经 LastError 直达用户。
    /// </summary>
    public IReadOnlyList<LspServerStatus> GetStatus()
    {
        var statuses = _servers.Values.Select(s => new LspServerStatus
        {
            Name = s.Config.Name,
            IsRunning = s.IsRunning,
            IsInitialized = s.IsInitialized,
            Capabilities = s.Capabilities,
            IsIndexing = s.IsIndexing
        }).ToList();

        // 软失败落池：启动失败的实例已从 _servers 移除，但失败原因必须可见（对齐 MCP LastError 语义），
        // 否则 /lsp status 只会显示 "No servers running"，模型无从自诊断。
        foreach (var (name, error) in _startErrors)
        {
            if (!statuses.Any(s => string.Equals(s.Name, name, StringComparison.Ordinal)))
            {
                statuses.Add(new LspServerStatus
                {
                    Name = name,
                    IsRunning = false,
                    IsInitialized = false,
                    Capabilities = null,
                    IsIndexing = false,
                    LastError = error
                });
            }
        }

        return statuses;
    }

    private async Task HealthCheckLoopAsync(CancellationToken ct)
    {
        var lastCleanup = DateTimeOffset.UtcNow;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(Constants.Lsp.HealthCheckInterval, ct).ConfigureAwait(false);

                foreach (var (name, instance) in _servers)
                {
                    if (!instance.IsRunning)
                        continue;

                    if (instance.IsHealthy)
                    {
                        // 恢复健康即清零重启计数（对齐 MCP 连接成功清零语义），
                        // 保证放弃后的服务器一旦恢复可重新获得完整的自愈配额。
                        _restartAttempts.TryRemove(name, out _);
                        continue;
                    }

                    // 崩溃恢复：与 MCP 自动重连同策略——指数退避 + 上限放弃。
                    var now = DateTimeOffset.UtcNow;
                    if (!_restartAttempts.TryGetValue(name, out var state))
                        _restartAttempts[name] = state = (0, DateTimeOffset.MinValue);

                    var backoff = TimeSpan.FromSeconds(Math.Min(30 * Math.Pow(2, state.Count), 240));
                    if (now - state.LastAttempt < backoff)
                        continue;
                    if (state.Count >= MaxCrashRestarts)
                    {
                        _logger.LogError(
                            "LSP server {ServerName} still unhealthy after {Attempts} restart attempts — giving up. Use /lsp restart to recover manually.",
                            name, state.Count);
                        continue;
                    }

                    _restartAttempts[name] = (state.Count + 1, now);
                    _logger.LogWarning(
                        "LSP server {ServerName} crashed — auto-restarting (attempt {Attempt}/{Max})",
                        name, state.Count + 1, MaxCrashRestarts);

                    _servers.TryRemove(name, out _);
                    try { await instance.StopAsync().ConfigureAwait(false); }
                    catch (Exception stopEx) { _logger.LogDebug(stopEx, "Failed to stop crashed LSP server {ServerName}", name); }

                    // 启动成功路径内部清零 _restartAttempts（StartServerAsync 成功即清零），此处无需重复。
                    if (await StartServerAsync(instance.Config, ct).ConfigureAwait(false))
                    {
                        _logger.LogInformation("LSP server {ServerName} auto-restarted successfully", name);
                    }
                }

                // Periodically clean up stale diagnostics to prevent unbounded growth
                // from files that were never explicitly closed via didClose.
                if (DateTimeOffset.UtcNow - lastCleanup >= Constants.Lsp.DiagnosticsCleanupInterval)
                {
                    _diagnosticRegistry.CleanupExpired(Constants.Lsp.DiagnosticsMaxAge);
                    lastCleanup = DateTimeOffset.UtcNow;
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Health check loop error");
            }
        }
    }

    /// <summary>崩溃自动重启的最大尝试次数，超过后提示用户手动 /lsp restart。</summary>
    private const int MaxCrashRestarts = 3;

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        _healthCheckCts.Cancel();

        if (_healthCheckTask != null)
        {
            try
            {
                await _healthCheckTask.WaitAsync(Constants.Lsp.ShutdownTimeout).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                /* expected cancellation */
            }
            catch (TimeoutException)
            {
                _logger.LogWarning("Health check task did not complete within timeout");
            }
        }

        _healthCheckCts.Dispose();

        foreach (var name in _servers.Keys.ToList())
        {
            try
            {
                await StopServerAsync(name).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error stopping LSP server {Name} during disposal", name);
            }
        }

        _servers.Clear();
    }
}
