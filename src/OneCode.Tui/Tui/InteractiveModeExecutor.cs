using OneCode.App.Services;
using OneCode.Core.Mcp;

namespace OneCode.Tui;

/// <summary>
/// Thin orchestrator for interactive TUI mode. Session bootstrap, keybindings,
/// and TUI context construction live in focused collaborators.
/// <see cref="ExecuteAsync"/> 绑定为组合根的 <see cref="InteractiveShellRunner"/> 委托。
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
            session, keyResolver, keyContextManager, null, out var runEventsBinder, ct);
        var exitCode = hostConfigurator.Run(ctx, session, slashCommandPipeline.CommandState, runEventsBinder, ct);
        await mcpConnectionManager.DisposeAsync().ConfigureAwait(false);
        return exitCode;
    }
}
