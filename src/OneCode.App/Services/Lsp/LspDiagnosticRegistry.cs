using OneCode.Core.Lsp;
using OneCode.Infrastructure.Text;
namespace OneCode.App.Services.Lsp;

/// <summary>
/// Collects and manages LSP diagnostics from multiple servers.
/// Listens to textDocument/publishDiagnostics notifications and stores them
/// for later retrieval by LspTool and other consumers.
/// </summary>
public sealed class LspDiagnosticRegistry : IDisposable
{
    private readonly ILogger<LspDiagnosticRegistry> _logger;
    private readonly ConcurrentDictionary<string, List<LspDiagnostic>> _diagnostics = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastUpdated = new();
    private readonly int _maxDiagnosticsPerFile;

    public LspDiagnosticRegistry(ILogger<LspDiagnosticRegistry>? logger = null, int maxDiagnosticsPerFile = 1000)
    {
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<LspDiagnosticRegistry>.Instance;
        _maxDiagnosticsPerFile = maxDiagnosticsPerFile;
    }

    /// <summary>
    /// Raised whenever diagnostics are updated via <see cref="ProcessDiagnostics"/>.
    /// Subscribers (e.g. the TUI status bar) use this to refresh their display.
    /// The event is fired on the LSP server's notification thread, so subscribers
    /// must marshal to the UI thread themselves (e.g. via <c>IApplication.Invoke</c>).
    /// </summary>
    public event Action? DiagnosticsChanged;

    /// <summary>
    /// Process a textDocument/publishDiagnostics notification from an LSP server.
    /// </summary>
    public void ProcessDiagnostics(string serverName, JsonElement publishDiagnosticsParams)
    {
        try
        {
            var uri = publishDiagnosticsParams.TryGetProperty("uri", out var uriProp)
                ? uriProp.GetString() ?? ""
                : "";

            if (string.IsNullOrEmpty(uri))
            {
                _logger.LogWarning("Received diagnostics without URI from server {ServerName}", serverName);
                return;
            }

            List<LspDiagnostic> diagnostics = [];
            if (publishDiagnosticsParams.TryGetProperty("diagnostics", out var diagArray))
            {
                foreach (var diag in diagArray.EnumerateArray())
                {
                    var diagnostic = ParseDiagnostic(serverName, uri, diag);
                    if (diagnostic != null)
                        diagnostics.Add(diagnostic);
                }
            }

            // Store diagnostics for this file
            var key = $"{serverName}:{uri}";
            _diagnostics[key] = diagnostics.Take(_maxDiagnosticsPerFile).ToList();
            _lastUpdated[key] = DateTimeOffset.UtcNow;

            _logger.LogDebug(
                "Received {Count} diagnostics for {Uri} from server {ServerName}",
                diagnostics.Count, uri, serverName);

            // Notify subscribers after the diagnostics have been stored.
            // ConcurrentDictionary is lock-free, so firing here is safe; subscribers
            // must marshal to their own thread (e.g. UI thread) if needed.
            DiagnosticsChanged?.Invoke();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to process diagnostics from server {ServerName}", serverName);
        }
    }

    /// <summary>
    /// Get all diagnostics for a specific server and file URI.
    /// </summary>
    public IReadOnlyList<LspDiagnostic> GetDiagnostics(string serverName, string? uri = null)
    {
        if (uri != null)
        {
            var key = $"{serverName}:{uri}";
            return _diagnostics.TryGetValue(key, out var diags) ? diags : Array.Empty<LspDiagnostic>();
        }

        var prefix = $"{serverName}:";
        return _diagnostics
            .Where(kv => kv.Key.StartsWith(prefix, StringComparison.Ordinal))
            .SelectMany(kv => kv.Value)
            .ToList();
    }

    /// <summary>
    /// Get all diagnostics across all servers.
    /// </summary>
    public IReadOnlyList<LspDiagnostic> GetAllDiagnostics()
    {
        return _diagnostics.Values.SelectMany(d => d).ToList();
    }

    /// <summary>
    /// Get diagnostic count for a specific file.
    /// </summary>
    public int GetDiagnosticCount(string serverName, string uri)
    {
        var key = $"{serverName}:{uri}";
        return _diagnostics.TryGetValue(key, out var diags) ? diags.Count : 0;
    }

    /// <summary>
    /// Clear diagnostics for a specific file.
    /// </summary>
    public void ClearDiagnostics(string serverName, string uri)
    {
        var key = $"{serverName}:{uri}";
        _diagnostics.TryRemove(key, out _);
        _lastUpdated.TryRemove(key, out _);
    }

    /// <summary>
    /// Clear all diagnostics.
    /// </summary>
    public void ClearAll()
    {
        _diagnostics.Clear();
        _lastUpdated.Clear();
    }

    /// <summary>
    /// Remove diagnostics that haven't been updated within the specified max age.
    /// Called periodically by LspServerManager to prevent unbounded growth from
    /// files that were never explicitly closed via didClose.
    /// </summary>
    public void CleanupExpired(TimeSpan maxAge)
    {
        var cutoff = DateTimeOffset.UtcNow - maxAge;
        foreach (var kv in _lastUpdated)
        {
            if (kv.Value < cutoff)
            {
                _diagnostics.TryRemove(kv.Key, out _);
                _lastUpdated.TryRemove(kv.Key, out _);
            }
        }
    }

    /// <summary>
    /// Get last update time for a file's diagnostics.
    /// </summary>
    public DateTimeOffset? GetLastUpdated(string serverName, string uri)
    {
        var key = $"{serverName}:{uri}";
        return _lastUpdated.TryGetValue(key, out var time) ? time : null;
    }

    /// <summary>
    /// Create a notification handler action for textDocument/publishDiagnostics.
    /// Use this with LspClient.OnNotification("textDocument/publishDiagnostics", handler).
    /// </summary>
    public Action<JsonElement> CreateHandler(string serverName)
    {
        return (p) => ProcessDiagnostics(serverName, p);
    }

    private LspDiagnostic? ParseDiagnostic(string serverName, string uri, JsonElement diag)
    {
        try
        {
            var severity = diag.TryGetProperty("severity", out var sev)
                ? (LspDiagnosticSeverity)(sev.GetInt32())
                : LspDiagnosticSeverity.Error;

            var message = diag.TryGetProperty("message", out var msg)
                ? msg.GetString() ?? ""
                : "";

            var range = diag.TryGetProperty("range", out var rangeProp)
                ? ParseRange(rangeProp)
                : new LspRange(0, 0, 0, 0);

            var source = diag.TryGetProperty("source", out var src)
                ? src.GetString()
                : null;

            var code = diag.TryGetProperty("code", out var codeProp)
                ? codeProp.ValueKind switch
                {
                    JsonValueKind.String => codeProp.GetString(),
                    JsonValueKind.Number => codeProp.GetInt64().ToString(CultureInfo.InvariantCulture),
                    _ => null
                }
                : null;

            return new LspDiagnostic
            {
                ServerName = serverName,
                Uri = uri,
                Severity = severity,
                Message = message,
                Range = range,
                Source = source,
                Code = code,
                Timestamp = DateTimeOffset.UtcNow
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse diagnostic from {ServerName}", serverName);
            return null;
        }
    }

    private static LspRange ParseRange(JsonElement range)
    {
        var startLine = range.TryGetProperty("start", out var start)
            ? (start.TryGetProperty("line", out var sl) ? sl.GetInt32() : 0)
            : 0;
        var startCol = range.TryGetProperty("start", out var start2)
            ? (start2.TryGetProperty("character", out var sc) ? sc.GetInt32() : 0)
            : 0;
        var endLine = range.TryGetProperty("end", out var end)
            ? (end.TryGetProperty("line", out var el) ? el.GetInt32() : 0)
            : 0;
        var endCol = range.TryGetProperty("end", out var end2)
            ? (end2.TryGetProperty("character", out var ec) ? ec.GetInt32() : 0)
            : 0;

        return new LspRange(startLine, startCol, endLine, endCol);
    }

    public void Dispose()
    {
        ClearAll();
    }
}

/// <summary>
/// Represents a single diagnostic from an LSP server.
/// </summary>
public sealed record LspDiagnostic
{
    public required string ServerName { get; init; }
    public required string Uri { get; init; }
    public required LspDiagnosticSeverity Severity { get; init; }
    public required string Message { get; init; }
    public required LspRange Range { get; init; }
    public string? Source { get; init; }
    public string? Code { get; init; }
    public DateTimeOffset Timestamp { get; init; }

    /// <summary>
    /// Get the file path from the URI using the centralised <see cref="LspUriHelper"/>,
    /// which correctly handles <c>file:///C:/...</c> triple-slash form and converts
    /// forward slashes to the platform-native separator.
    /// </summary>
    public string FilePath => LspUriHelper.UriToFilePath(Uri);

    /// <summary>
    /// Get a human-readable summary.
    /// </summary>
    public string Summary => $"{Source ?? ServerName} [{Severity}] {FilePath}:{Range.StartLine + 1}:{Range.StartColumn + 1}: {Message}";
}

/// <summary>
/// Represents a range in a text document (0-based line and column).
/// </summary>
public sealed record LspRange(int StartLine, int StartColumn, int EndLine, int EndColumn);

