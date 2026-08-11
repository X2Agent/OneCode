using OneCode.Core.Lsp;
using OneCode.Infrastructure.Config;

namespace OneCode.App.Services.Lsp;

/// <summary>
/// Manages multiple LSP server instances — lifecycle, health checks, and diagnostic collection.
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
            return true;
        }
        catch (Exception ex)
        {
            _servers.TryRemove(config.Name, out _);
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

        await instance.SendNotificationAsync(method, parameters).ConfigureAwait(false);
    }

    public async Task BroadcastNotificationAsync(string method, JsonElement parameters)
    {
        foreach (var instance in _servers.Values)
        {
            try
            {
                if (instance.IsInitialized)
                    await instance.SendNotificationAsync(method, parameters).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to send notification to LSP server {Name}", instance.Config.Name);
            }
        }
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
    /// Get status of all servers.
    /// </summary>
    public IReadOnlyList<LspServerStatus> GetStatus()
    {
        return _servers.Values.Select(s => new LspServerStatus
        {
            Name = s.Config.Name,
            IsRunning = s.IsRunning,
            IsInitialized = s.IsInitialized,
            Capabilities = s.Capabilities
        }).ToList();
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
                    if (instance.IsRunning && !instance.IsHealthy)
                    {
                        _logger.LogWarning("LSP server {ServerName} is unhealthy", name);
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
