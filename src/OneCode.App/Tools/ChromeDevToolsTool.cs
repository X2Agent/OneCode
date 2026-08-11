using System.ComponentModel;
using Microsoft.Playwright;

namespace OneCode.App.Tools;

/// <summary>
/// Chrome DevTools 集成工具 — 通过 CDP (Playwright) 与运行中的 Chrome 交互。
/// 支持：截图、控制台日志、网络请求、JavaScript 求值。
/// 浏览器生命周期由 <see cref="BrowserLauncher"/> 管理。
/// </summary>
public sealed class ChromeDevToolsTool : IAsyncDisposable
{
    private readonly SemaphoreSlim _pageLock = new(1, 1);
    private readonly BrowserLauncher _browserLauncher;
    private readonly ILogger<ChromeDevToolsTool> _logger;
    private IPage? _activePage;

    private const int DefaultTimeoutMs = 30_000;
    private const int MaxConsoleEntries = 100;
    private const int MaxNetworkEntries = 50;

    public ChromeDevToolsTool(BrowserLauncher browserLauncher, ILogger<ChromeDevToolsTool> logger)
    {
        _browserLauncher = browserLauncher ?? throw new ArgumentNullException(nameof(browserLauncher));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    [Description("Interact with a Chrome browser via DevTools Protocol. Actions: screenshot, console, network, evaluate.")]
    public async Task<ToolResult> ExecuteDevToolsAsync(
        [Description("The DevTools action: screenshot, console, network, evaluate")] string action,
        [Description("URL to navigate to before the action (for screenshot)")] string? url = null,
        [Description("Capture full page screenshot (for screenshot, default false)")] bool fullPage = false,
        [Description("JavaScript expression to evaluate (for evaluate)")] string? expression = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(action))
            return ToolResult.Error("action is required: screenshot, console, network, evaluate");

        try
        {
            await EnsurePageAsync(ct).ConfigureAwait(false);

            return action.ToLowerInvariant() switch
            {
                "screenshot" => await TakeScreenshotAsync(url, fullPage, ct).ConfigureAwait(false),
                "console" => await GetConsoleLogsAsync(ct).ConfigureAwait(false),
                "network" => await GetNetworkRequestsAsync(ct).ConfigureAwait(false),
                "evaluate" => await EvaluateJavaScriptAsync(expression).ConfigureAwait(false),
                _ => ToolResult.Error($"Unknown action '{action}'. Supported: screenshot, console, network, evaluate."),
            };
        }
        catch (OperationCanceledException)
        {
            return ToolResult.Error("Operation cancelled.");
        }
        catch (PlaywrightException ex) when (ex.Message.Contains("Target closed", StringComparison.OrdinalIgnoreCase))
        {
            _activePage = null;
            return ToolResult.Error("Browser tab was closed. Please re-open a tab and try again.");
        }
        catch (Exception ex)
        {
            return ToolResult.Error($"Chrome DevTools error: {ex.Message}");
        }
    }

    private async Task EnsurePageAsync(CancellationToken ct)
    {
        if (_activePage is { IsClosed: false })
            return;

        await _pageLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_activePage is { IsClosed: false })
                return;

            var browser = await _browserLauncher.GetBrowserAsync(ct).ConfigureAwait(false);
            if (browser is null)
                throw new InvalidOperationException("Browser not available. Ensure Playwright Chromium is installed.");

            var context = await browser.NewContextAsync(new BrowserNewContextOptions
            {
                ViewportSize = new ViewportSize { Width = 1280, Height = 720 },
            }).ConfigureAwait(false);

            _activePage = await context.NewPageAsync().ConfigureAwait(false);
        }
        finally
        {
            _pageLock.Release();
        }
    }

    private async Task<ToolResult> TakeScreenshotAsync(string? url, bool fullPage, CancellationToken ct)
    {
        if (_activePage is null)
            return ToolResult.Error("No active browser page. Navigate to a URL first.");

        if (!string.IsNullOrWhiteSpace(url))
        {
            var fullUrl = url!.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                          url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                ? url : $"https://{url}";
            await _activePage.GotoAsync(fullUrl, new PageGotoOptions
            {
                Timeout = DefaultTimeoutMs,
                WaitUntil = WaitUntilState.NetworkIdle,
            }).ConfigureAwait(false);
        }

        var screenshotBytes = await _activePage.ScreenshotAsync(new PageScreenshotOptions
        {
            FullPage = fullPage,
            Type = ScreenshotType.Png,
        }).ConfigureAwait(false);

        var base64 = Convert.ToBase64String(screenshotBytes);
        return ToolResult.Success($"Screenshot captured ({screenshotBytes.Length / 1024}KB). Data follows as base64 PNG.\n\ndata:image/png;base64,{base64}");
    }

    private async Task<ToolResult> GetConsoleLogsAsync(CancellationToken ct)
    {
        if (_activePage is null)
            return ToolResult.Error("No active browser page.");

        List<string> messages = [];
        _activePage.Console += (_, msg) =>
        {
            if (messages.Count < MaxConsoleEntries)
                messages.Add($"[{msg.Type}] {msg.Text}");
        };

        await _activePage.ReloadAsync(new PageReloadOptions { WaitUntil = WaitUntilState.NetworkIdle }).ConfigureAwait(false);
        await Task.Delay(1000, ct).ConfigureAwait(false);

        var sb = new System.Text.StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"Console logs ({messages.Count} entries):");
        sb.AppendLine("---");
        foreach (var msg in messages) sb.AppendLine(msg);
        return ToolResult.Success(sb.ToString());
    }

    private async Task<ToolResult> GetNetworkRequestsAsync(CancellationToken ct)
    {
        if (_activePage is null)
            return ToolResult.Error("No active browser page.");

        List<(string method, string url, int status)> requests = [];
        _activePage.Request += (_, req) =>
        {
            if (requests.Count < MaxNetworkEntries)
                requests.Add((req.Method, req.Url, 0));
        };
        _activePage.Response += (_, resp) =>
        {
            for (var i = requests.Count - 1; i >= 0; i--)
            {
                if (requests[i].url == resp.Url && requests[i].status == 0)
                {
                    requests[i] = (requests[i].method, requests[i].url, resp.Status);
                    break;
                }
            }
        };

        await _activePage.ReloadAsync(new PageReloadOptions { WaitUntil = WaitUntilState.NetworkIdle }).ConfigureAwait(false);
        await Task.Delay(500, ct).ConfigureAwait(false);

        var sb = new System.Text.StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"Network requests ({requests.Count} recorded):");
        sb.AppendLine("---");
        foreach (var (method, url, status) in requests)
            sb.AppendLine(CultureInfo.InvariantCulture, $"{method} {(status > 0 ? status.ToString(CultureInfo.InvariantCulture) : "pending")} {url}");
        return ToolResult.Success(sb.ToString());
    }

    private async Task<ToolResult> EvaluateJavaScriptAsync(string? expression)
    {
        if (_activePage is null)
            return ToolResult.Error("No active browser page.");

        if (string.IsNullOrWhiteSpace(expression))
            return ToolResult.Error("expression is required for evaluate action.");

        var result = await _activePage.EvaluateAsync<object?>(expression).ConfigureAwait(false);
        var resultText = result switch
        {
            null => "undefined",
            string s => s,
            JsonElement je => je.ToString(),
            _ => result.ToString() ?? "object"
        };

        return ToolResult.Success($"JavaScript evaluation result:\n{resultText}");
    }

    public async ValueTask DisposeAsync()
    {
        _pageLock.Dispose();
        if (_activePage is not null)
        {
            try { await _activePage.CloseAsync().ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogWarning(ex, "ChromeDevToolsTool Dispose page close failed"); }
        }
        GC.SuppressFinalize(this);
    }
}
