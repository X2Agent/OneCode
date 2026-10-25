using OneCode.Core.Config;
using System.Runtime.InteropServices;
using OneCode.App.Services.Hooks;
using OneCode.Core.Product;
using OneCode.Infrastructure;
using OneCode.Core.IO;
using OneCode.Core.Lsp;
using OneCode.Infrastructure.Config;
using OneCode.Infrastructure.Persistence;
using OneCode.Infrastructure.Media;
using OneCode.Infrastructure.Setup;

namespace OneCode.App.Services;

/// <summary>
/// Owns one-time host warm-up: process cache, hot-reload watchers,
/// image cleanup, code index, hook bootstrap + config hot-reload, and ToolCatalog cache.
/// Hosted-service start remains at the composition root (<c>IHost.StartAsync</c>).
/// </summary>
public sealed class AppStartupService(
    IProcessRunner processRunner,
    CodeIndexHotReloader codeIndexHotReloader,
    ImagePipeline imagePipeline,
    ICodeIndexService codeIndexService,
    HookConfigHotReloader hookHotReloader,
    IToolCatalog toolCatalog,
    IConfigManager configManager,
    ILogger<OneCodeApp> logger)
{
    private bool _warmedUp;

    public async Task WarmUpAsync(CancellationToken ct)
    {
        if (_warmedUp) return;

        await processRunner.WarmCommandCacheAsync("pwsh", "rg", "git").ConfigureAwait(false);

        codeIndexHotReloader.StartWatching(Environment.CurrentDirectory);

        _ = imagePipeline.CleanupOldFilesAsync(TimeSpan.FromHours(24), ct)
            .ContinueWith(
                t => logger.LogWarning(t.Exception!, "Image temp-dir cleanup failed"),
                TaskContinuationOptions.OnlyOnFaulted);

        // DurableFile 崩溃残留清扫与自更新 .old 旧可执行文件清理
        UpgradeService.TryCleanupStaleOldExecutable(Environment.ProcessPath, logger);
        var durableRoot = PathsHelper.GetUserConfigDir();
        _ = Task.Run(
            () =>
            {
                var removed = DurableFile.SweepTempResidue(durableRoot, TimeSpan.FromHours(1));
                if (removed > 0)
                    logger.LogInformation("Swept {Count} durable-file temp residue file(s) under {Root}", removed, durableRoot);
            },
            ct).ContinueWith(
            t => logger.LogWarning(t.Exception!, "Durable-file residue sweep failed"),
            TaskContinuationOptions.OnlyOnFaulted);

        _ = codeIndexService.BuildIndexAsync(Environment.CurrentDirectory, ct);

        var configDir = PathsHelper.GetUserConfigDir();
        var projectConfigDir = Path.Combine(
            Environment.CurrentDirectory,
            Constants.App.ConfigDirName);
        try
        {
            hookHotReloader.BootstrapAndStartWatching(configDir, projectConfigDir);
        }
        catch (Exception ex)
        {
            // Per-hook errors are already skipped inside Bootstrap; this catches unexpected
            // loader failures so ToolCatalog warm-up still runs.
            logger.LogWarning(ex, "Hook bootstrap failed; continuing warm-up");
        }

        _ = toolCatalog.Tools;

        configManager.InitializeWatcher();

        _warmedUp = true;
    }

    public static void LogStartupBanner(ILogger logger)
    {
        logger.LogInformation("{ProductName} starting...", ProductInfo.Default.Name);
        logger.LogInformation("OS: {OS} Runtime: .NET {Runtime}",
            RuntimeInformation.OSDescription, Environment.Version);
        logger.LogInformation("Working directory: {Cwd}", Environment.CurrentDirectory);
    }
}
