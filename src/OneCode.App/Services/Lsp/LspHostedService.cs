using Microsoft.Extensions.Hosting;
using OneCode.Core.Lsp;

namespace OneCode.App.Services.Lsp;

/// <summary>
/// Hosted service that starts LSP servers for enabled language packs on application startup
/// and stops all servers on shutdown. Server start failures are logged but do not block startup.
/// </summary>
public sealed class LspHostedService(
    LanguagePackRegistry registry,
    ILspServerManager serverManager,
    LanguagePackInstaller installer,
    IWorkingDirectoryAccessor workingDirectoryAccessor,
    IStartupHintCollector hintCollector,
    IHostApplicationLifetime lifetime,
    ILogger<LspHostedService> logger) : IHostedService
{
    // Tracked so StopAsync can await it. Previously this was a fire-and-forget
    // (_ = StartServersAsync()), which violated the project's async规范: startup
    // failures were swallowed as unobserved exceptions and the host could exit
    // mid-startup during a fast shutdown.
    private Task _startTask = Task.CompletedTask;

    /// <summary>
    /// Defers LSP server startup until <see cref="IHostApplicationLifetime.ApplicationStarted"/>
    /// so the session working directory (set via --workspace or /cwd) is ready before being
    /// passed to LSP servers — without it, launching from bin/Debug would make servers index
    /// the build output folder instead of the user's project root.
    /// Startup runs in the background; the Task is tracked for observation by StopAsync.
    /// </summary>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _startTask = StartAfterApplicationStartedAsync();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Stop all running LSP servers on shutdown. Awaits the startup task first so a fast
    /// shutdown during LSP init doesn't race StopServerAsync against StartServerAsync.
    /// </summary>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        // Observe startup task exceptions to prevent unobserved-task-exception warnings.
        // StartServersAsync already logs its own exceptions, so swallow here.
        try
        {
            await _startTask.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogTrace(ex, "Observed startup task exception");
        }

        foreach (var status in serverManager.GetStatus())
        {
            try
            {
                await serverManager.StopServerAsync(status.Name, cancellationToken).ConfigureAwait(false);
                logger.LogInformation("Stopped LSP server {Name} during shutdown", status.Name);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to stop LSP server {Name} during shutdown", status.Name);
            }
        }
    }

    /// <summary>
    /// Waits for <see cref="IHostApplicationLifetime.ApplicationStarted"/> then starts servers.
    /// Using a TaskCompletionSource (instead of a fire-and-forget callback) lets us await
    /// the full startup chain from <see cref="StopAsync"/>.
    /// </summary>
    private async Task StartAfterApplicationStartedAsync()
    {
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        lifetime.ApplicationStarted.Register(() => started.TrySetResult(true));
        await started.Task.ConfigureAwait(false);
        await StartServersAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Start servers for all registered packs. A server is started only when:
    ///   1. Its binary is installed (probed via DetectionCommand), AND
    ///   2. The session working directory looks like a project of that language
    ///      (matched against <see cref="LanguagePack.ProjectFiles"/>).
    /// Packs without <see cref="LanguagePack.ProjectFiles"/> configured will not start.
    /// </summary>
    private async Task StartServersAsync()
    {
        try
        {
            // Use the session's working directory (set via --workspace or /cwd) rather than the
            // process CWD. Without this, launching OneCode.Cli from its bin/Debug directory causes
            // LSP servers to index the build output folder instead of the user's project root.
            var sessionWorkingDir = workingDirectoryAccessor.WorkingDirectory;

            foreach (var pack in registry.GetAllPacks())
            {
                try
                {
                    // Check project match FIRST: it's a fast filesystem check, and it lets us
                    // distinguish two very different skip reasons:
                    //   - project doesn't match → not actionable, skip silently
                    //   - project matches but binary missing → highly actionable, push a hint
                    if (!LspProjectMatcher.Matches(pack, sessionWorkingDir, logger))
                    {
                        logger.LogDebug(
                            "Skipping LSP server for {PackId}: no project marker files in {Dir}",
                            pack.Id, sessionWorkingDir);
                        continue;
                    }

                    // Probe the server binary before attempting to start it.
                    var installed = await installer.IsInstalledAsync(pack.Id).ConfigureAwait(false);
                    if (!installed)
                    {
                        logger.LogInformation(
                            "Skipping LSP server for {PackId}: binary not installed (run /lsp install {PackId} to enable)",
                            pack.Id, pack.Id);

                        // Push an actionable hint to the TUI so the user knows they're missing out
                        // on language intelligence for THIS project. Without it, the only signal is
                        // an Information-level log that's invisible in the default TUI log level.
                        hintCollector.Add(new StartupHint
                        {
                            Id = $"lsp-missing-{pack.Id}",
                            Message = $"检测到 {pack.DisplayName} 项目，但语言服务器 {pack.Server.Command} 未安装。运行 /lsp install {pack.Id} 可启用语义代码智能（跳转定义、查找引用、诊断等）。",
                            ActionCommand = $"/lsp install {pack.Id}",
                        });
                        continue;
                    }

                    logger.LogInformation("Starting LSP server for {PackId}...", pack.Id);
                    var config = pack.ToServerConfig() with { WorkingDirectory = sessionWorkingDir };
                    var started = await serverManager.StartServerAsync(config, CancellationToken.None).ConfigureAwait(false);
                    if (started)
                        logger.LogInformation("LSP server for {PackId} started successfully", pack.Id);
                    else
                        logger.LogWarning("Failed to start LSP server for {PackId}", pack.Id);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Error starting LSP server for {PackId}", pack.Id);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error in LSP server startup background task");
        }
    }
}
