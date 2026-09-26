using OneCode.App.Tools;

using OneCode.Infrastructure.Config;
using OneCode.Infrastructure.Text;

using OneCode.Core.Lsp;

namespace OneCode.App.Services.Lsp;

public sealed class LspNotifier(
    IEnhancedLspService lspService,
    LspDiagnosticRegistry diagnosticRegistry,
    ILogger<LspNotifier> logger) : ILspNotifier
{
    /// <summary>
    /// didChange 后诊断发布等待的轮询节奏（可注入先例同 <c>McpConnectionManager.AutoReconnectInterval</c>）：
    /// 轮询新诊断而非单次 sleep——大项目首次分析远超固定 settle 时间，假阴性会误导模型。
    /// internal 可变静态，供测试注入加速轮询。
    /// </summary>
    internal static int PollIntervalMs = 200;
    internal static int WaitTotalMs = 2000;

    /// <summary>
    /// 服务器首次索引（$/progress 进行中）时的等待预算上限：大项目首次分析远超固定窗口，
    /// 此时报 null 是假阴性、会误导模型。索引结束预算即回落 WaitTotalMs；窗口始终有上界
    /// （Write/Edit 完成路径同步 await 此处，绝不无界等待）。internal 可变静态供测试注入。
    /// </summary>
    internal static int IndexingWaitTotalMs = 10_000;

    private readonly IEnhancedLspService _lspService = lspService;
    private readonly LspDiagnosticRegistry _diagnosticRegistry = diagnosticRegistry;
    private readonly ILogger<LspNotifier> _logger = logger;

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

    public async Task NotifyDirectoryDeletedAsync(string directoryPath, CancellationToken ct = default)
    {
        try
        {
            await _lspService.NotifyDirectoryDeletedAsync(directoryPath, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to notify LSP of directory deletion: {Path}", directoryPath);
        }
    }

    /// <summary>
    /// Wait for the LSP server to publish <b>fresh</b> diagnostics after a didChange,
    /// then return a concise summary of diagnostics for the given file.
    /// Freshness baseline is the didChange send time recorded by
    /// <see cref="EnhancedLspService.NotifyFileUpdatedAsync"/> (via GetLastDidChangeUtc) —
    /// not the call time: diagnostics pushed while the write pipeline was still running
    /// would otherwise be misjudged as stale. Files that never went through didChange
    /// (read-only analysis paths) fall back to the call time.
    /// Uses polling (fresh = registry timestamp newer than baseline) with check-before-wait.
    /// Returns null if no server is running, or on timeout without fresh diagnostics —
    /// stale (previous-version) diagnostics are never presented as the current result.
    /// </summary>
    public async Task<string?> GetDiagnosticsSummaryAsync(string fullPath, CancellationToken ct = default)
    {
        try
        {
            // 无运行中的 LSP 服务器时立即返回：该文件永远等不到新诊断，
            // 轮询只会白等整个窗口（Write/Edit 完成路径每次都要付出这个延迟）。
            if (!_lspService.HasRunningServer)
                return null;

            var uri = LspUriHelper.BuildFileUri(fullPath);
            // 新鲜度基线：didChange 发送时刻（EnhancedLspService 发送前置位记录）。
            // 快路径推送在基线之后到达，不会被误判 stale；未推送过的文件回落到调用时刻，
            // 此时既有诊断按定义旧于基线，超时后返回 null 而非旧数据。
            var baseline = _lspService.GetLastDidChangeUtc(fullPath) ?? DateTimeOffset.UtcNow;

            IReadOnlyList<LspDiagnostic> fresh = [];
            var waited = 0;
            while (true)
            {
                // 先查后等：多数场景新诊断已就绪，避免固定首跳延迟。
                fresh = _diagnosticRegistry.GetAllDiagnostics()
                    .Where(d => string.Equals(d.Uri, uri, StringComparison.OrdinalIgnoreCase))
                    .Where(d => d.Timestamp >= baseline)
                    .ToList();

                // 等待预算按当前状态取值：服务器首次索引中（$/progress 进行中）延长到
                // IndexingWaitTotalMs——大项目首次分析远超固定窗口，此时返回 null 是假阴性；
                // 索引结束即回落 WaitTotalMs。窗口始终有上界，不会无界等待。
                var budget = _lspService.IsIndexing ? IndexingWaitTotalMs : WaitTotalMs;

                // 新版本诊断已到达——立即返回；超时仍未等到则退出（见下）。
                if (fresh.Count > 0 || waited >= budget)
                    break;

                await Task.Delay(PollIntervalMs, ct).ConfigureAwait(false);
                waited += PollIntervalMs;
            }

            // 超时且无新版本诊断：返回 null，不把上一版本 stale 诊断当结果误导模型。
            if (fresh.Count == 0)
                return null;

            var errors = fresh.Count(d => d.Severity == LspDiagnosticSeverity.Error);
            var warnings = fresh.Count(d => d.Severity == LspDiagnosticSeverity.Warning);
            var hints = fresh.Count(d => d.Severity is LspDiagnosticSeverity.Information or LspDiagnosticSeverity.Hint);

            List<string> parts = [];
            if (errors > 0) parts.Add($"{errors} error(s)");
            if (warnings > 0) parts.Add($"{warnings} warning(s)");
            if (hints > 0) parts.Add($"{hints} hint(s)");

            var header = $"LSP diagnostics: {string.Join(", ", parts)}";

            // Show up to 5 most severe diagnostics to keep the tool result concise
            var top = fresh
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
