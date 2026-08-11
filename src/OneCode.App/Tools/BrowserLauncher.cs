using Microsoft.Playwright;

namespace OneCode.App.Tools;

/// <summary>
/// 共享的 Playwright 浏览器生命周期管理——负责浏览器初始化、锁定和释放。
/// PlaywrightRenderer 和 ChromeDevToolsTool 共用同一个实例。
/// </summary>
public sealed class BrowserLauncher : IAsyncDisposable
{
    private readonly ILogger<BrowserLauncher> _logger;
    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private bool _initFailed;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public BrowserLauncher(ILogger<BrowserLauncher> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// 获取已初始化的浏览器实例。首次调用时启动 Chromium。
    /// 初始化失败后不会重试（通常是缺少浏览器二进制文件）。
    /// 返回 null 表示浏览器不可用，调用方应回退到降级路径。
    /// </summary>
    public async Task<IBrowser?> GetBrowserAsync(CancellationToken ct = default)
    {
        if (_browser is { IsConnected: true }) return _browser;
        if (_initFailed) return null;

        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_browser is { IsConnected: true }) return _browser;
            if (_initFailed) return null;

            _playwright = await Playwright.CreateAsync().ConfigureAwait(false);
            _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
            {
                Headless = true,
                Args = ["--no-sandbox", "--disable-gpu", "--disable-dev-shm-usage"],
            }).ConfigureAwait(false);

            return _browser;
        }
        catch (Exception ex)
        {
            _initFailed = true;
            _logger.LogError(ex,
                "Failed to initialize Playwright (browser launch). " +
                "Run 'playwright install chromium' if browser binaries are missing.");
            return null;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_browser is not null)
        {
            try { await _browser.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogWarning(ex, "BrowserLauncher DisposeAsync failed"); }
        }
        _playwright?.Dispose();
        _lock.Dispose();
    }
}
