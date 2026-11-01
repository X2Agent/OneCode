using OneCode.Core.Config;
using System.Runtime.InteropServices;
using OneCode.App.Services.Hooks;
using OneCode.Core.Product;
using OneCode.Infrastructure;
using OneCode.Core.IO;
using OneCode.Infrastructure.Config;
using OneCode.Infrastructure.Persistence;
using OneCode.Infrastructure.Media;
using OneCode.Infrastructure.Setup;

namespace OneCode.App.Services;

/// <summary>
/// Owns one-time host warm-up: process cache, hot-reload watchers, image cleanup,
/// hook bootstrap + config hot-reload. Hosted-service start remains at the composition
/// root (<c>IHost.StartAsync</c>).
/// </summary>
/// <remarks>
/// 预热只做「首帧必须就绪」的事：命令缓存、监视器、hook 引导。全仓代码索引与工具目录合并
/// 都是昂贵且只在首次使用时才需要的，留给各自的首次调用按需完成——放在首帧之前只会推迟
/// 界面出现，不改变任何可用性。
/// </remarks>
public sealed class AppStartupService(
    IProcessRunner processRunner,
    CodeIndexHotReloader codeIndexHotReloader,
    ImagePipeline imagePipeline,
    HookConfigHotReloader hookHotReloader,
    IConfigManager configManager,
    ILogger<OneCodeApp> logger)
{
    private bool _warmedUp;

    public async Task WarmUpAsync(CancellationToken ct)
    {
        if (_warmedUp) return;

        await processRunner.WarmCommandCacheAsync("pwsh", "rg", "git").ConfigureAwait(false);

        codeIndexHotReloader.StartWatching(Environment.CurrentDirectory);

        // 预热阶段的昂贵工作一律延后到首次真实使用：
        //  - 全仓代码索引：仅在工具首次查询符号时按需构建；
        //  - ToolCatalog 合并：仅在装配 agent 工具表时求值。
        // 把它们放在首帧之前只会推迟界面出现，不改变任何可用性。
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
            // loader failures so the remaining warm-up still runs.
            logger.LogWarning(ex, "Hook bootstrap failed; continuing warm-up");
        }

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
