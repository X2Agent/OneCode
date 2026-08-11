using Microsoft.Playwright;

namespace OneCode.App.Tools;

/// <summary>
/// 使用 Playwright 渲染 JS 页面并提取文本内容。
/// 浏览器生命周期由 <see cref="BrowserLauncher"/> 管理。
/// </summary>
public sealed class PlaywrightRenderer
{
    private readonly BrowserLauncher _browserLauncher;
    private readonly ILogger<PlaywrightRenderer> _logger;

    public PlaywrightRenderer(BrowserLauncher browserLauncher, ILogger<PlaywrightRenderer> logger)
    {
        _browserLauncher = browserLauncher ?? throw new ArgumentNullException(nameof(browserLauncher));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<string?> RenderAsync(string url, int timeoutMs = 30000, CancellationToken ct = default)
    {
        var browser = await _browserLauncher.GetBrowserAsync(ct).ConfigureAwait(false);
        if (browser == null)
            return null;

        try
        {
            var page = await browser.NewPageAsync().ConfigureAwait(false);

            var response = await page.GotoAsync(url, new PageGotoOptions
            {
                Timeout = timeoutMs,
                WaitUntil = WaitUntilState.NetworkIdle,
            }).ConfigureAwait(false);

            if (response == null || !response.Ok)
            {
                _logger.LogDebug("Playwright Goto returned non-OK response for {Url} (status {Status})",
                    url, response?.Status);
                await page.CloseAsync().ConfigureAwait(false);
                return null;
            }

            var content = await page.InnerTextAsync("body").ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(content))
                content = await page.ContentAsync().ConfigureAwait(false);

            await page.CloseAsync().ConfigureAwait(false);
            return content;
        }
        catch (TimeoutException ex)
        {
            _logger.LogDebug(ex, "Playwright render timed out for {Url} after {TimeoutMs}ms", url, timeoutMs);
            return null;
        }
        catch (PlaywrightException ex)
        {
            _logger.LogDebug(ex, "Playwright render failed for {Url}", url);
            return null;
        }
    }
}
