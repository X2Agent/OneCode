// MAAI001 suppressed: AIContextProvider uses experimental MAF APIs
using System.Text;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OneCode.App.Services.Lsp;
using OneCode.Core.Lsp;

namespace OneCode.App.Services.Context;

/// <summary>
/// Injects current LSP diagnostics (Error/Warning only) into the agent context as a system message.
///
/// <para>
/// This is the Layer 3 (Context Provider) of the LSP integration: it passively enhances the
/// agent's awareness of static analysis issues without requiring the agent to explicitly call
/// the Lsp tool. The provider is a no-op when no LSP servers are running or when there are no
/// Error/Warning diagnostics for files under the current working directory.
/// </para>
///
/// <para>
/// Diagnostics are capped at 30 entries (max 10 per file) to avoid context budget bloat.
/// Information and Hint severities are intentionally excluded — they add noise without
/// actionable value for the agent.
/// </para>
/// </summary>
public sealed class LspDiagnosticContextProvider : ReadOnlyAIContextProviderBase
{
    private readonly LspDiagnosticRegistry _diagnosticRegistry;
    private readonly ILogger<LspDiagnosticContextProvider> _logger;
    private readonly string _workingDirectory;

    private const int MaxTotalDiagnostics = 30;
    private const int MaxPerFile = 10;

    // Cache the injected diagnostics signature so that unchanged diagnostics
    // are not re-injected on every AI turn — saves context budget when the
    // agent is discussing non-code topics and the diagnostic set is stable.
    private int _lastSignature;
    private string? _lastInjectedMessage;

    public LspDiagnosticContextProvider(
        LspDiagnosticRegistry diagnosticRegistry,
        ILogger<LspDiagnosticContextProvider> logger,
        string workingDirectory)
    {
        _diagnosticRegistry = diagnosticRegistry ?? throw new ArgumentNullException(nameof(diagnosticRegistry));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _workingDirectory = workingDirectory ?? throw new ArgumentNullException(nameof(workingDirectory));
    }

    protected override ValueTask<AIContext> ProvideAIContextAsync(
        AIContextProvider.InvokingContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var allDiagnostics = _diagnosticRegistry.GetAllDiagnostics();
            if (allDiagnostics.Count == 0)
            {
                // Reset cache when diagnostics clear so a future re-occurrence is re-injected.
                _lastSignature = 0;
                _lastInjectedMessage = null;
                return ValueTask.FromResult(new AIContext());
            }

            // Filter: Error/Warning only, files under working directory
            var relevant = allDiagnostics
                .Where(d => d.Severity is LspDiagnosticSeverity.Error or LspDiagnosticSeverity.Warning)
                .Where(d => IsUnderWorkingDirectory(d.FilePath))
                .Take(MaxTotalDiagnostics)
                .ToList();

            if (relevant.Count == 0)
            {
                _lastSignature = 0;
                _lastInjectedMessage = null;
                return ValueTask.FromResult(new AIContext());
            }

            // Compute a stable signature over the relevant diagnostics. Only
            // the fields that affect the rendered message are included so that
            // cosmetic changes (e.g. diagnostic ordering from the server) don't
            // trigger needless re-injection.
            var signature = ComputeSignature(relevant);
            if (signature == _lastSignature && _lastInjectedMessage is not null)
            {
                // Unchanged — re-inject the cached message without rebuilding it.
                return ValueTask.FromResult(new AIContext
                {
                    Messages = [new ChatMessage(ChatRole.System, _lastInjectedMessage)],
                });
            }

            var message = BuildMessage(relevant);
            _lastSignature = signature;
            _lastInjectedMessage = message;

            _logger.LogDebug("Injected {Count} LSP diagnostics into context (signature changed)", relevant.Count);

            return ValueTask.FromResult(new AIContext
            {
                Messages = [new ChatMessage(ChatRole.System, message)],
            });
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to provide LSP diagnostic context");
            return ValueTask.FromResult(new AIContext());
        }
    }

    /// <summary>
    /// Compute a 32-bit signature from the relevant diagnostics. Includes
    /// severity, file path, line, source, and message — the same fields that
    /// appear in the rendered system message.
    /// </summary>
    private static int ComputeSignature(List<LspDiagnostic> diagnostics)
    {
        var hash = new HashCode();
        foreach (var d in diagnostics)
        {
            hash.Add(d.Severity);
            hash.Add(d.FilePath);
            hash.Add(d.Range.StartLine);
            hash.Add(d.Source ?? d.ServerName);
            hash.Add(d.Message);
        }
        return hash.ToHashCode();
    }

    private string BuildMessage(List<LspDiagnostic> relevant)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## Active LSP Diagnostics (Error/Warning)");
        sb.AppendLine("The following static analysis issues were reported by language servers for files in this project:");
        sb.AppendLine();

        foreach (var group in relevant.GroupBy(d => d.FilePath))
        {
            var fileName = GetRelativePath(group.Key);
            sb.AppendLine(CultureInfo.InvariantCulture, $"### {fileName}");
            foreach (var d in group.Take(MaxPerFile))
            {
                var severity = d.Severity == LspDiagnosticSeverity.Error ? "ERROR" : "WARN";
                var source = d.Source ?? d.ServerName;
                sb.AppendLine(CultureInfo.InvariantCulture, $"  [{severity}] L{d.Range.StartLine + 1}: {d.Message} ({source})");
            }
            sb.AppendLine();
        }

        sb.AppendLine("Consider addressing these issues. Use the Lsp tool with action 'diagnostics' for full details.");
        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Check if a file path is under the working directory.
    /// Handles path separator differences (Windows vs Unix).
    /// </summary>
    private bool IsUnderWorkingDirectory(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return false;

        try
        {
            var fullPath = Path.GetFullPath(filePath);
            var fullWorkingDir = Path.GetFullPath(_workingDirectory);
            return fullPath.StartsWith(fullWorkingDir, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to resolve path {FilePath} against working directory", filePath);
            return false;
        }
    }

    private string GetRelativePath(string filePath)
    {
        try
        {
            var relative = Path.GetRelativePath(_workingDirectory, filePath);
            return relative.StartsWith("..", StringComparison.Ordinal) ? Path.GetFileName(filePath) : relative;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to compute relative path for {FilePath}, falling back to file name", filePath);
            return Path.GetFileName(filePath);
        }
    }
}
