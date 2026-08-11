namespace OneCode.App.Services;

/// <summary>
/// Shared mutable state for the slash-command pipeline, extracted to eliminate
/// closure captures so <c>TryResolvePromptCommandAsync</c> / <c>ExecuteCommandAsync</c>
/// can live as instance methods on <see cref="InteractiveModeExecutor"/> rather
/// than as local functions inside <c>ExecuteAsync</c>.
/// </summary>
/// <remarks>
/// Holds two pieces of state that previously lived as locals captured by closures:
/// <list type="bullet">
/// <item><c>_cachedNonPromptResult</c> — caches a non-PromptResult command result
/// produced by <c>TryResolvePromptCommandAsync</c> so <c>ExecuteCommandAsync</c>
/// can return it without re-executing the command.</item>
/// <item><c>_refreshSessionUi</c> — callback set inside <c>TuiHost.Run</c> factory
/// (by <see cref="TuiHostConfigurator"/>) and invoked by command execution after
/// session-affecting commands (e.g. <c>/session</c>).</item>
/// </list>
/// Session-scoped mutable bridge (not a DI defect): the UI refresh callback is only
/// available after the TUI host wires the session surface.
/// </remarks>
public sealed class CommandExecutionState
{
    private CommandResult? _cachedNonPromptResult;
    private Func<CancellationToken, Task>? _refreshSessionUi;

    /// <summary>Caches a non-PromptResult command result for later consumption.</summary>
    public void CacheResult(CommandResult result) => _cachedNonPromptResult = result;

    /// <summary>Consumes and clears the cached result, if any.</summary>
    public CommandResult? ConsumeCachedResult()
    {
        var cached = _cachedNonPromptResult;
        _cachedNonPromptResult = null;
        return cached;
    }

    /// <summary>Sets the session-UI refresh callback (called by TuiHostConfigurator).</summary>
    public void SetRefreshUiCallback(Func<CancellationToken, Task>? cb) => _refreshSessionUi = cb;

    /// <summary>Invokes the session-UI refresh callback, if registered.</summary>
    public Task RefreshSessionUiAsync(CancellationToken ct)
        => _refreshSessionUi?.Invoke(ct) ?? Task.CompletedTask;
}
