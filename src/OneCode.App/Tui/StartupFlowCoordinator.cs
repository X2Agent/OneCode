namespace OneCode.App.Tui;

/// <summary>
/// Coordinates the interactive startup screen before the main REPL shell starts.
/// Currently wires only the workspace trust step.
/// </summary>
/// <remarks>
/// 可选依赖（保留可空）：
/// - <see cref="_logger"/>：仅记录 trust 拒绝日志；缺失时静默
/// </remarks>
public sealed class StartupFlowCoordinator
{
    private readonly Func<bool> _shouldShowTrustPrompt;
    private readonly Func<CancellationToken, Task<bool>> _ensureTrustAsync;
    private readonly ILogger<StartupFlowCoordinator>? _logger;

    public StartupFlowCoordinator(
        TrustService trustService,
        ILogger<StartupFlowCoordinator>? logger = null)
        : this(trustService.ShouldShowTrustPrompt, trustService.EnsureTrustAsync, logger)
    {
    }

    internal StartupFlowCoordinator(
        Func<bool> shouldShowTrustPrompt,
        Func<CancellationToken, Task<bool>> ensureTrustAsync,
        ILogger<StartupFlowCoordinator>? logger = null)
    {
        _shouldShowTrustPrompt = shouldShowTrustPrompt;
        _ensureTrustAsync = ensureTrustAsync;
        _logger = logger;
    }

    public async Task<StartupFlowResult> RunInteractiveAsync(CancellationToken ct = default)
    {
        if (!_shouldShowTrustPrompt())
        {
            return StartupFlowResult.Continue();
        }

        // Use console prompt directly — never create a temporary TUI Application
        // before TuiHost.Run, as Terminal.Gui does not support multiple instances.
        var accepted = await _ensureTrustAsync(ct).ConfigureAwait(false);

        if (!accepted)
        {
            _logger?.LogInformation("Interactive startup flow aborted because workspace trust was declined.");
            return StartupFlowResult.Exit();
        }

        return StartupFlowResult.Continue();
    }
}

public sealed record StartupFlowResult(bool ShouldContinue, bool TrustConfirmed)
{
    public static StartupFlowResult Continue() => new(true, true);

    public static StartupFlowResult Exit() => new(false, false);
}
