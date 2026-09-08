using System.ComponentModel;
using OneCode.Core.Errors;
using OneCode.Core.Mcp;
using OneCode.Infrastructure.Mcp;

namespace OneCode.App.Tools;

/// <summary>
/// 浏览器抓取工具 — 以能力命名的一等工具（与 WebFetch 对仗），模型感知不到 MCP。
///
/// <para>一次调用封装完整链路：SSRF 校验 → 按需连接内置 playwright MCP 服务
/// （见 <see cref="BuiltInMcpServers"/>，不随启动连接）→ browser_navigate +
/// browser_snapshot 渲染 → 返回页面 ARIA 快照。首次连接可能启动 npx 进程并
/// 下载 Chromium，副作用通过 <see cref="OneCode.Core.Tools.ToolResult"/> 的
/// Dynamic 风险 Conditional 审批对用户可见；连接失败返回结构化 503，由模型
/// 向用户报告或继续使用 WebFetch 的结果。</para>
///
/// <para>playwright MCP 暴露单一共享浏览器会话：navigate+snapshot 必须串行，
/// 否则并发调用会互相覆盖页面状态（<see cref="_sessionGate"/>）。</para>
/// </summary>
public sealed class BrowserFetchTool
{
    /// <summary>内置 playwright 服务名（<see cref="BuiltInMcpServers"/> 预置）。</summary>
    public const string ServerName = "playwright";

    public const string NavigateToolName = "browser_navigate";
    public const string SnapshotToolName = "browser_snapshot";

    private const int DefaultTimeoutMs = 30_000;
    private const int MaxSnapshotLength = 100_000;

    private readonly IMcpConnectionManager _connectionManager;
    private readonly ILogger<BrowserFetchTool> _logger;
    private readonly SemaphoreSlim _sessionGate;

    public BrowserFetchTool(
        IMcpConnectionManager connectionManager,
        ILogger<BrowserFetchTool> logger)
        : this(connectionManager, logger, new SemaphoreSlim(1, 1))
    {
    }

    /// <summary>测试构造器：注入 gate 以断言共享会话串行化，无需真实 MCP 连接。</summary>
    internal BrowserFetchTool(
        IMcpConnectionManager connectionManager,
        ILogger<BrowserFetchTool> logger,
        SemaphoreSlim sessionGate)
    {
        _connectionManager = connectionManager;
        _logger = logger;
        _sessionGate = sessionGate;
    }

    [Description("Fetch a web page in a real (headless) browser and return its accessibility snapshot. " +
                 "Use for JavaScript-only SPA pages, pages behind anti-bot protection, or when WebFetch " +
                 "returned a JavaScript-rendering hint. Returns the page as an accessibility tree " +
                 "(headings, links, buttons, text). Same SSRF protection as WebFetch: localhost, private IPs, " +
                 "internal hostnames, and private DNS records are blocked. First call may take a while " +
                 "(launches the browser; Chromium may be downloaded once).")]
    public async Task<ToolResult> FetchAsync(
        [Description("The URL to fetch. Must be http(s) and not point to a private/loopback host.")] string url,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return ToolResult.Error(AgentProblemDetails.ToolExecutionFailed(
                "url is required", toolName: "BrowserFetch",
                suggestedNextAction: "传入要抓取的完整 http(s) URL"));
        }

        if (!FetchSafetyPolicy.ValidateUrl(url))
        {
            return ToolResult.Error($"Invalid URL: {url}");
        }

        // 与 WebFetch 同一套 DNS rebinding 预解析：主机名解析出私网地址即拒绝，
        // 避免浏览器进程成为绕过 WebFetch SSRF 防护的旁路。
        var dnsBlock = await FetchSafetyPolicy.CheckDnsRebindingAsync(url, _logger, ct).ConfigureAwait(false);
        if (dnsBlock is not null)
        {
            return ToolResult.Error(dnsBlock);
        }

        // 共享浏览器会话：连接与渲染整体串行，避免并发调用互相覆盖页面。
        await _sessionGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var client = await EnsureConnectedAsync(ct).ConfigureAwait(false);
            if (client is null)
            {
                return ToolResult.Error(AgentProblemDetails.ServiceUnavailable(
                        $"Browser rendering service ('{ServerName}') is not available.",
                        toolName: "BrowserFetch"))
                    with
                {
                    SuggestedNextAction = "浏览器服务未配置、已禁用或启动失败；向用户报告无法进行浏览器渲染，或改用 WebFetch 的结果"
                };
            }

            var snapshot = await RenderAsync(client, url, ct).ConfigureAwait(false);
            if (snapshot is null)
            {
                return ToolResult.Error(AgentProblemDetails.ToolExecutionFailed(
                        $"Failed to render '{url}' in the browser.", toolName: "BrowserFetch"))
                    with
                {
                    SuggestedNextAction = "检查页面是否可正常访问；或向用户报告该页面无法渲染"
                };
            }

            return ToolResult.Success(snapshot);
        }
        finally
        {
            _sessionGate.Release();
        }
    }

    /// <summary>已连接直接复用；未连接按需连接（gate 内串行触发，避免重复拉起进程）。</summary>
    private async Task<IMcpClient?> EnsureConnectedAsync(CancellationToken ct)
    {
        var client = ResolveClient();
        if (client is not null)
            return client;

        try
        {
            var connected = await _connectionManager.ConnectOneAsync(ServerName, ct).ConfigureAwait(false);
            if (connected)
            {
                _logger.LogInformation(
                    "On-demand connect of built-in Playwright MCP server '{Server}' (npx -y @playwright/mcp@latest). " +
                    "First use may download Chromium and take a while.", ServerName);
            }
            return connected ? ResolveClient() : null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "On-demand connect of Playwright MCP failed");
            return null;
        }
    }

    private async Task<string?> RenderAsync(IMcpClient client, string url, CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(DefaultTimeoutMs);

        try
        {
            var navigate = await client.CallToolAsync(
                NavigateToolName,
                new Dictionary<string, object?> { ["url"] = url },
                timeoutCts.Token).ConfigureAwait(false);
            if (navigate.IsError)
            {
                _logger.LogDebug("Playwright MCP {Tool} failed for {Url}: {Content}",
                    NavigateToolName, url, TruncateForLog(navigate.Content));
                return null;
            }

            var snapshot = await client.CallToolAsync(
                SnapshotToolName,
                arguments: null,
                timeoutCts.Token).ConfigureAwait(false);
            if (snapshot.IsError || string.IsNullOrWhiteSpace(snapshot.Content))
            {
                _logger.LogDebug("Playwright MCP {Tool} failed or empty for {Url}: {Content}",
                    SnapshotToolName, url, TruncateForLog(snapshot.Content));
                return null;
            }

            var content = snapshot.Content.Trim();
            return content.Length <= MaxSnapshotLength
                ? content
                : content[..MaxSnapshotLength] + "\n\n[Content truncated due to length...]";
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogDebug("Playwright MCP render timed out after {TimeoutMs}ms for {Url}", DefaultTimeoutMs, url);
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

    /// <summary>优先常规 'playwright' 名；否则第一个名字含 playwright 的已连接客户端。</summary>
    private IMcpClient? ResolveClient()
    {
        var preferred = _connectionManager.GetClient(ServerName);
        if (preferred is not null)
            return preferred;

        foreach (var (name, client) in _connectionManager.GetConnectedClients())
        {
            if (name.Contains(ServerName, StringComparison.OrdinalIgnoreCase))
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