using OneCode.Core.Lsp;
using OneCode.Infrastructure.Text;
namespace OneCode.App.Services.Lsp;

public sealed class EnhancedLspService : IAsyncDisposable
{
    private readonly LspServerManager _serverManager;
    private readonly LspDiagnosticRegistry _diagnosticRegistry;
    private readonly ILogger<EnhancedLspService> _logger;
    // File sync state: tracks version numbers for open files (absent key = not opened yet)
    private readonly ConcurrentDictionary<string, int> _fileVersions = new();

    public EnhancedLspService(
        LspServerManager serverManager,
        LspDiagnosticRegistry diagnosticRegistry,
        ILogger<EnhancedLspService> logger)
    {
        _serverManager = serverManager;
        _diagnosticRegistry = diagnosticRegistry;
        _logger = logger;
    }

    public Task<bool> StartServerAsync(LspServerConfig config, CancellationToken ct = default) =>
        _serverManager.StartServerAsync(config, ct);

    public Task<bool> StopServerAsync(string name, CancellationToken ct = default) =>
        _serverManager.StopServerAsync(name, ct);

    public async Task<string?> GetCompletionsAsync(
        string serverName, string filePath, int line, int column, CancellationToken ct = default)
    {
        var parameters = JsonSerializer.Serialize(new
        {
            textDocument = new { uri = LspUriHelper.BuildFileUri(filePath) },
            position = new { line, character = column },
        });

        var result = await _serverManager.SendRequestAsync(serverName, "textDocument/completion", ParseJson(parameters), ct).ConfigureAwait(false);
        return result?.GetRawText();
    }

    public async Task<string?> GetDefinitionAsync(
        string serverName, string filePath, int line, int column, CancellationToken ct = default)
    {
        var parameters = JsonSerializer.Serialize(new
        {
            textDocument = new { uri = LspUriHelper.BuildFileUri(filePath) },
            position = new { line, character = column },
        });

        var result = await _serverManager.SendRequestAsync(serverName, "textDocument/definition", ParseJson(parameters), ct).ConfigureAwait(false);
        return result?.GetRawText();
    }

    public async Task<string?> GetReferencesAsync(
        string serverName, string filePath, int line, int column, CancellationToken ct = default)
    {
        var parameters = JsonSerializer.Serialize(new
        {
            textDocument = new { uri = LspUriHelper.BuildFileUri(filePath) },
            position = new { line, character = column },
            context = new { includeDeclaration = true },
        });

        var result = await _serverManager.SendRequestAsync(serverName, "textDocument/references", ParseJson(parameters), ct).ConfigureAwait(false);
        return result?.GetRawText();
    }

    public async Task<string?> GetHoverAsync(
        string serverName, string filePath, int line, int column, CancellationToken ct = default)
    {
        var parameters = JsonSerializer.Serialize(new
        {
            textDocument = new { uri = LspUriHelper.BuildFileUri(filePath) },
            position = new { line, character = column },
        });

        var result = await _serverManager.SendRequestAsync(serverName, "textDocument/hover", ParseJson(parameters), ct).ConfigureAwait(false);
        return result?.GetRawText();
    }

    public async Task<string?> GetDocumentSymbolsAsync(
        string serverName, string filePath, CancellationToken ct = default)
    {
        var parameters = JsonSerializer.Serialize(new
        {
            textDocument = new { uri = LspUriHelper.BuildFileUri(filePath) },
        });

        var result = await _serverManager.SendRequestAsync(serverName, "textDocument/documentSymbol", ParseJson(parameters), ct).ConfigureAwait(false);
        return result?.GetRawText();
    }

    public IReadOnlyList<LspDiagnostic> GetDiagnostics(string? serverName = null, string? uri = null) =>
        _diagnosticRegistry.GetDiagnostics(serverName ?? "", uri);

    /// <summary>
    /// Notify an LSP server that a file has been closed. Sends textDocument/didClose
    /// and removes the file from the version tracking map so a subsequent open
    /// starts fresh at version 1.
    /// </summary>
    public async Task NotifyFileClosedAsync(string serverName, string filePath, CancellationToken ct = default)
    {
        var uri = LspUriHelper.BuildFileUri(filePath);
        _fileVersions.TryRemove(uri, out _);

        var didCloseParams = JsonSerializer.Serialize(new
        {
            textDocument = new { uri }
        });

        try
        {
            await _serverManager.SendNotificationAsync(
                serverName, "textDocument/didClose", ParseJson(didCloseParams)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send didClose for {Path} to {Server}", filePath, serverName);
        }
    }

    /// <summary>
    /// Notify LSP servers that a file has been updated.
    /// Implements proper LSP text document synchronization:
    ///   - First time seeing a file → send textDocument/didOpen with full text
    ///   - Subsequent updates → send textDocument/didChange with full text (full sync mode)
    /// Previous implementation sent didChange with text=null and never sent didOpen,
    /// which caused servers to reject the notification per LSP spec.
    /// </summary>
    public async Task NotifyFileUpdatedAsync(string filePath, CancellationToken ct = default)
    {
        _logger.LogDebug("File updated notification: {Path}", filePath);

        var uri = LspUriHelper.BuildFileUri(filePath);

        // Read current file content — LSP didOpen/didChange requires full text
        string content;
        try
        {
            content = await File.ReadAllTextAsync(filePath, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not read file for LSP notification: {Path}", filePath);
            return;
        }

        var languageId = GetLanguageId(filePath);

        try
        {
            if (!_fileVersions.ContainsKey(uri))
            {
                _fileVersions[uri] = 1;
                var didOpenParams = JsonSerializer.Serialize(new
                {
                    textDocument = new { uri, languageId, version = 1, text = content }
                });
                await _serverManager.BroadcastNotificationAsync(
                    "textDocument/didOpen", ParseJson(didOpenParams)).ConfigureAwait(false);
            }
            else
            {
                // Full sync mode: send the entire file content in contentChanges
                var version = _fileVersions.AddOrUpdate(uri, 2, (_, v) => v + 1);
                var didChangeParams = JsonSerializer.Serialize(new
                {
                    textDocument = new { uri, version },
                    contentChanges = new[] { new { text = content } }
                });
                await _serverManager.BroadcastNotificationAsync(
                    "textDocument/didChange", ParseJson(didChangeParams)).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to forward file notification for {Path}", filePath);
        }
    }

    /// <summary>
    /// Map file extension to LSP language identifier.
    /// </summary>
    private static string GetLanguageId(string filePath)
    {
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        return ext switch
        {
            ".cs" => "csharp",
            ".ts" => "typescript",
            ".tsx" => "typescriptreact",
            ".js" => "javascript",
            ".jsx" => "javascriptreact",
            ".py" => "python",
            ".go" => "go",
            ".rs" => "rust",
            ".java" => "java",
            ".c" or ".h" => "c",
            ".cpp" or ".hpp" or ".cc" => "cpp",
            ".rb" => "ruby",
            ".lua" => "lua",
            ".php" => "php",
            ".json" => "json",
            ".yaml" or ".yml" => "yaml",
            ".md" => "markdown",
            _ => "plaintext"
        };
    }

    public IReadOnlyList<LspServerStatus> GetServerStatus() => _serverManager.GetStatus();

    public async ValueTask DisposeAsync()
    {
        if (_serverManager is IAsyncDisposable asyncDisposable)
            await asyncDisposable.DisposeAsync().ConfigureAwait(false);
    }

    private static JsonElement ParseJson(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }
}
