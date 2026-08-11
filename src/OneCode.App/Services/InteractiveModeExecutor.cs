using OneCode.Infrastructure.Mcp;

namespace OneCode.App.Services;

/// <summary>
/// Thin orchestrator for interactive TUI mode. Session bootstrap, keybindings,
/// and TUI context construction live in focused collaborators.
/// </summary>
public sealed class InteractiveModeExecutor(
    InteractiveBootstrapService bootstrap,
    InteractiveKeybindingService keybindings,
    TuiContextFactory tuiContextFactory,
    TuiHostConfigurator hostConfigurator,
    IMcpConnectionManager mcpConnectionManager,
    SlashCommandPipeline slashCommandPipeline,
    WorkingModeBridgeFactory workingModeBridgeFactory)
{
    public bool IsExitRequested => slashCommandPipeline.IsExitRequested;

    /// <summary>
    /// Entry point: initialize the session, build TUI context, and run the
    /// interactive host until the user quits. Returns the process exit code.
    /// </summary>
    public async Task<int> ExecuteAsync(CancellationToken ct)
    {
        var session = await bootstrap.InitializeAsync(ct).ConfigureAwait(false);
        if (session is null) return 1;

        var (keyResolver, keyContextManager) = await keybindings.InitializeAsync(ct).ConfigureAwait(false);

        using var modeBridge = workingModeBridgeFactory.Create(session.ModeController);
        modeBridge.SyncInitialState();

        var ctx = tuiContextFactory.Create(
            session, keyResolver, keyContextManager, null, out var emitEventBinder, ct);
        var exitCode = hostConfigurator.Run(ctx, session, slashCommandPipeline.CommandState, emitEventBinder, ct);
        await mcpConnectionManager.DisposeAsync().ConfigureAwait(false);
        return exitCode;
    }
}
