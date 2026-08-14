using OneCode.Infrastructure.Mcp;

namespace OneCode.App.Tools;

/// <summary>
/// Renders pages via the built-in Playwright MCP server (navigate + snapshot).
/// Does not use <c>IToolCatalog</c> — only <see cref="IMcpConnectionManager"/>.
///
/// Playwright MCP exposes a single shared browser session; navigate+snapshot must be
/// serialized so concurrent WebFetch SPA fallbacks do not clobber each other's page.
///
/// 按需连接：playwright 是内置 MCP 服务，不随启动连接（见
/// <see cref="McpConnectionManager.ConnectAllAsync"/>）；首次 RenderAsync 找不到
/// 已连接客户端时调用 <see cref="IMcpConnectionManager.ConnectOneAsync"/> 按需连接，
/// 失败静默返回 null，由调用方（WebFetchTool）保留 HTTP 渲染结果。
/// </summary>
public sealed class McpBrowserGateway : IBrowserPageRenderer
{
    /// <summary>内置 playwright 服务名（BuiltInMcpServers 预置）。</summary>
    public const string DefaultServerName = "playwright";

    public const string NavigateToolName = "browser_navigate";
    public const string SnapshotToolName = "browser_snapshot";

    // 30s — matches prior in-process PlaywrightRenderer timeout for SPA fallback.
    private const int DefaultTimeoutMs = 30_000;

    private readonly IMcpConnectionManager _mcpManager;
    private readonly ILogger<McpBrowserGateway> _logger;
    private readonly SemaphoreSlim _sessionGate;
    private readonly Func<string, int, CancellationToken, Task<string?>>? _renderUnderGate;

    public McpBrowserGateway(
        IMcpConnectionManager mcpManager,
        ILogger<McpBrowserGateway> logger)
        : this(mcpManager, logger, new SemaphoreSlim(1, 1), renderUnderGate: null)
    {
    }

    /// <summary>
    /// Test constructor: inject a gate + optional render body to assert mutual exclusion
    /// without a live Playwright MCP connection.
    /// </summary>
    internal McpBrowserGateway(
        IMcpConnectionManager mcpManager,
        ILogger<McpBrowserGateway> logger,
        SemaphoreSlim sessionGate,
        Func<string, int, CancellationToken, Task<string?>>? renderUnderGate)
    {
        _mcpManager = mcpManager;
        _logger = logger;
        _sessionGate = sessionGate;
        _renderUnderGate = renderUnderGate;
    }

    public async Task<string?> RenderAsync(
        string url,
        int timeoutMs = DefaultTimeoutMs,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;

        // Serialize all navigate+snapshot pairs on the shared MCP browser session.
        // 按需连接也在 gate 内执行：并发 WebFetch 首次触发时只有一个连接尝试，
        // 避免重复启动 playwright MCP 进程。
        await _sessionGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_renderUnderGate is not null)
                return await _renderUnderGate(url, timeoutMs, ct).ConfigureAwait(false);

            if (ResolvePlaywrightClient() is null && !await TryConnectAsync(ct).ConfigureAwait(false))
            {
                _logger.LogDebug("No connected Playwright MCP server; skip browser render for {Url}", url);
                return null;
            }

            return await RenderWithMcpAsync(url, timeoutMs, ct).ConfigureAwait(false);
        }
        finally
        {
            _sessionGate.Release();
        }
    }

    private async Task<bool> TryConnectAsync(CancellationToken ct)
    {
        try
        {
            var connected = await _mcpManager.ConnectOneAsync(DefaultServerName, ct).ConfigureAwait(false);
            if (connected)
            {
                // 副作用告知：按需连接会启动内置 playwright MCP（npx -y @playwright/mcp@latest），
                // 首次使用可能下载 Chromium，耗时较长。明示用户这不是 WebFetch 卡死，
                // 而是浏览器渲染兜底正在拉取外部依赖。
                _logger.LogInformation(
                    "WebFetch SPA fallback triggered on-demand connect of the built-in Playwright MCP server " +
                    "'{Server}' (npx -y @playwright/mcp@latest). First use may download Chromium and take a while.",
                    DefaultServerName);
            }
            return connected;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "On-demand connect of Playwright MCP threw; skip browser render");
            return false;
        }
    }

    private async Task<string?> RenderWithMcpAsync(string url, int timeoutMs, CancellationToken ct)
    {
        var client = ResolvePlaywrightClient();
        if (client is null)
        {
            _logger.LogDebug("No connected Playwright MCP server; skip browser render for {Url}", url);
            return null;
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (timeoutMs > 0)
            timeoutCts.CancelAfter(timeoutMs);

        try
        {
            var navigate = await client.CallToolAsync(
                NavigateToolName,
                new Dictionary<string, object?> { ["url"] = url },
                timeoutCts.Token).ConfigureAwait(false);

            if (navigate.IsError)
            {
                _logger.LogDebug(
                    "Playwright MCP {Tool} failed for {Url}: {Content}",
                    NavigateToolName, url, TruncateForLog(navigate.Content));
                return null;
            }

            var snapshot = await client.CallToolAsync(
                SnapshotToolName,
                arguments: null,
                timeoutCts.Token).ConfigureAwait(false);

            if (snapshot.IsError || string.IsNullOrWhiteSpace(snapshot.Content))
            {
                _logger.LogDebug(
                    "Playwright MCP {Tool} failed or empty for {Url}: {Content}",
                    SnapshotToolName, url, TruncateForLog(snapshot.Content));
                return null;
            }

            return snapshot.Content.Trim();
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogDebug("Playwright MCP render timed out after {TimeoutMs}ms for {Url}", timeoutMs, url);
            return null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Playwright MCP render failed for {Url}", url);
            return null;
        }
    }

    /// <summary>
    /// Prefer the conventional <c>playwright</c> server; otherwise use the first connected
    /// client whose status name contains "playwright" (case-insensitive).
    /// </summary>
    private McpClient? ResolvePlaywrightClient()
    {
        var preferred = _mcpManager.GetClient(DefaultServerName);
        if (preferred is not null)
            return preferred;

        foreach (var (name, client) in _mcpManager.GetConnectedClients())
        {
            if (name.Contains(DefaultServerName, StringComparison.OrdinalIgnoreCase))
                return client;
        }

        return null;
    }

    private static string TruncateForLog(string? content)
    {
        if (string.IsNullOrEmpty(content))
            return "";
        const int max = 200;
        return content.Length <= max ? content : content[..max] + "…";
    }
}
