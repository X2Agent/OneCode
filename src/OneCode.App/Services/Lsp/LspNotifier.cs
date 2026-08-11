using OneCode.App.Tools;
using OneCode.Core.Lsp;
using OneCode.Infrastructure.Config;
using OneCode.Infrastructure.Text;

namespace OneCode.App.Services.Lsp;

public sealed class LspNotifier : ILspNotifier
{
    private readonly EnhancedLspService _lspService;
    private readonly LspDiagnosticRegistry _diagnosticRegistry;
    private readonly ILogger<LspNotifier> _logger;

    public LspNotifier(
        EnhancedLspService lspService,
        LspDiagnosticRegistry diagnosticRegistry,
        ILogger<LspNotifier> logger)
    {
        _lspService = lspService;
        _diagnosticRegistry = diagnosticRegistry;
        _logger = logger;
    }

    public async Task NotifyFileUpdatedAsync(string fullPath, CancellationToken ct = default)
    {
        try
        {
            await _lspService.NotifyFileUpdatedAsync(fullPath, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to notify LSP of file update: {Path}", fullPath);
        }
    }

    /// <summary>
    /// Wait briefly for the LSP server to publish fresh diagnostics after a didChange,
    /// then return a concise summary of diagnostics for the given file.
    /// Returns null if no diagnostics exist or no server is running.
    /// </summary>
    public async Task<string?> GetDiagnosticsSummaryAsync(string fullPath, CancellationToken ct = default)
    {
        try
        {
            // LSP diagnostics are published asynchronously after didChange.
            // Without a short delay we would read stale results from the previous version.
            await Task.Delay(Constants.Lsp.DiagnosticsSettleMs, ct).ConfigureAwait(false);

            var uri = LspUriHelper.BuildFileUri(fullPath);
            var diags = _diagnosticRegistry.GetAllDiagnostics()
                .Where(d => string.Equals(d.Uri, uri, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (diags.Count == 0)
                return null;

            var errors = diags.Count(d => d.Severity == LspDiagnosticSeverity.Error);
            var warnings = diags.Count(d => d.Severity == LspDiagnosticSeverity.Warning);
            var hints = diags.Count(d => d.Severity is LspDiagnosticSeverity.Information or LspDiagnosticSeverity.Hint);

            var parts = new List<string>();
            if (errors > 0) parts.Add($"{errors} error(s)");
            if (warnings > 0) parts.Add($"{warnings} warning(s)");
            if (hints > 0) parts.Add($"{hints} hint(s)");

            var header = $"LSP diagnostics: {string.Join(", ", parts)}";

            // Show up to 5 most severe diagnostics to keep the tool result concise
            var top = diags
                .OrderBy(d => (int)d.Severity)
                .Take(Constants.Lsp.MaxDiagnosticsInSummary)
                .Select(d => $"  [{d.Severity}] L{d.Range.StartLine + 1}: {d.Message}")
                .ToList();

            return top.Count > 0 ? $"{header}\n{string.Join("\n", top)}" : header;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to get diagnostics summary for: {Path}", fullPath);
            return null;
        }
    }
}
