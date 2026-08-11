using System.Runtime.InteropServices;
using OneCode.App.Services.Hooks;
using OneCode.Core.Coordinator;
using OneCode.Core.Product;
using OneCode.Infrastructure;
using OneCode.Infrastructure.Abstractions;
using OneCode.Infrastructure.Config;
using OneCode.Infrastructure.Media;

namespace OneCode.App.Services;

/// <summary>
/// Owns one-time host warm-up: process cache, builtin teams, hot-reload watchers,
/// image cleanup, code index, hook bootstrap, and ToolCatalog cache.
/// Hosted-service start remains at the composition root (<c>IHost.StartAsync</c>).
/// </summary>
public sealed class AppStartupService(
    IProcessRunner processRunner,
    ITeamOrchestrationService teamOrchestration,
    CodeIndexHotReloader codeIndexHotReloader,
    ImagePipeline imagePipeline,
    ICodeIndexService codeIndexService,
    HookConfigBootstrapper hookBootstrapper,
    IToolCatalog toolCatalog,
    IConfigManager configManager,
    ILogger<OneCodeApp> logger)
{
    private bool _warmedUp;

    public async Task WarmUpAsync(CancellationToken ct)
    {
        if (_warmedUp) return;

        await processRunner.WarmCommandCacheAsync("pwsh", "rg", "git").ConfigureAwait(false);

        await teamOrchestration.RegisterBuiltinTeamsAsync(ct).ConfigureAwait(false);

        codeIndexHotReloader.StartWatching(Environment.CurrentDirectory);

        _ = imagePipeline.CleanupOldFilesAsync(TimeSpan.FromHours(24), ct)
            .ContinueWith(
                t => logger.LogWarning(t.Exception!, "Image temp-dir cleanup failed"),
                TaskContinuationOptions.OnlyOnFaulted);

        _ = codeIndexService.BuildIndexAsync(Environment.CurrentDirectory, ct);

        var configDir = PathsHelper.GetUserConfigDir();
        var projectConfigDir = Path.Combine(
            Environment.CurrentDirectory,
            Constants.App.ConfigDirName);
        try
        {
            hookBootstrapper.Bootstrap(configDir, projectConfigDir);
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
