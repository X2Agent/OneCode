using OneCode.App.Tools;

using OneCode.Infrastructure.Config;
using OneCode.Infrastructure.Text;

using OneCode.Core.Lsp;

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

    public async Task NotifyFileClosedAsync(string fullPath, CancellationToken ct = default)
    {
        try
        {
            await _lspService.NotifyFileClosedAsync(fullPath, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to notify LSP of file close: {Path}", fullPath);
        }
    }

    /// <summary>
    /// Wait for the LSP server to publish <b>fresh</b> diagnostics after a didChange,
    /// then return a concise summary of diagnostics for the given file.
    /// Uses polling (registry timestamp newer than call time) instead of a fixed
    /// sleep — large projects can take far longer than any static settle delay,
    /// and a premature read yields a false "no diagnostics" answer.
    /// Returns null if no diagnostics exist or no server is running.
    /// </summary>
    public async Task<string?> GetDiagnosticsSummaryAsync(string fullPath, CancellationToken ct = default)
    {
        try
        {
            // 无运行中的 LSP 服务器时立即返回：该文件永远等不到新诊断，
            // 轮询只会白等整个窗口（Write/Edit 完成路径每次都要付出这个延迟）。
            if (!_lspService.HasRunningServer)
                return null;

            var calledAt = DateTimeOffset.UtcNow;
            var uri = LspUriHelper.BuildFileUri(fullPath);

            IReadOnlyList<LspDiagnostic> diags = [];
            var waited = 0;
            while (waited <= Constants.Lsp.DiagnosticsWaitTotalMs)
            {
                await Task.Delay(Constants.Lsp.DiagnosticsPollIntervalMs, ct).ConfigureAwait(false);
                waited += Constants.Lsp.DiagnosticsPollIntervalMs;

                diags = _diagnosticRegistry.GetAllDiagnostics()
                    .Where(d => string.Equals(d.Uri, uri, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                // 新诊断已到达（时间戳晚于调用时刻）——立即返回，不空等剩余窗口。
                if (diags.Any(d => d.Timestamp >= calledAt))
                    break;
            }

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
