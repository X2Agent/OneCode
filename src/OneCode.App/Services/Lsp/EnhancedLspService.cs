
using OneCode.Infrastructure.Text;
using OneCode.Core.Lsp;

namespace OneCode.App.Services.Lsp;

public sealed class EnhancedLspService : IEnhancedLspService, IAsyncDisposable
{
    private readonly LspServerManager _serverManager;
    private readonly LspDiagnosticRegistry _diagnosticRegistry;
    private readonly ILogger<EnhancedLspService> _logger;
    // File sync state: tracks version numbers for open files (absent key = not opened yet)
    private readonly ConcurrentDictionary<string, int> _fileVersions = new();
    // 每个 open 文档最近一次 didChange/didOpen 的发送时刻（P2 诊断新鲜度基线）。
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastDidChangeUtc = new(StringComparer.Ordinal);

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
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            // File deleted while open in the server — close it so servers stop
            // publishing diagnostics for a path that no longer exists.
            _logger.LogDebug("File {Path} no longer exists — sending didClose", filePath);
            await NotifyFileClosedAsync(filePath, ct).ConfigureAwait(false);
            return;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not read file for LSP notification: {Path}", filePath);
            return;
        }

        var languageId = GetLanguageId(filePath);

        // 新鲜度基线：didChange/didOpen 发送前置位。服务端诊断推送（快路径，不等待本次
        // 广播返回）只会发生在此之后，GetDiagnosticsSummaryAsync 以此判定"新版本诊断"，
        // 不会把推送窗口内合法到达的诊断误判为 stale。
        _lastDidChangeUtc[uri] = DateTimeOffset.UtcNow;

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
    /// Notify all servers that a file was closed (deleted or no longer tracked):
    /// sends <c>textDocument/didClose</c> and drops the open-file version state so a
    /// later recreate starts a fresh didOpen. Without this, servers keep publishing
    /// diagnostics for deleted files and full-sync state goes stale.
    /// </summary>
    public async Task NotifyFileClosedAsync(string filePath, CancellationToken ct = default)
    {
        var uri = LspUriHelper.BuildFileUri(filePath);
        if (!_fileVersions.TryRemove(uri, out _))
            return; // never opened — nothing to close

        _lastDidChangeUtc.TryRemove(uri, out _);
        try
        {
            var didCloseParams = JsonSerializer.Serialize(new { textDocument = new { uri } });
            await _serverManager.BroadcastNotificationAsync(
                "textDocument/didClose", ParseJson(didCloseParams)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to send didClose for {Path}", filePath);
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

    /// <summary>是否有任何 LSP 服务器处于运行中。诊断等待的短路依据：无服务器时永远等不到新诊断。</summary>
    public bool HasRunningServer => _serverManager.GetStatus().Any(s => s.IsRunning);

    /// <summary>是否有运行中的服务器正在索引（$/progress 进行中）。诊断等待预算的自适应延长依据。</summary>
    public bool IsIndexing => _serverManager.GetStatus().Any(s => s.IsRunning && s.IsIndexing);

    /// <summary>
    /// 指定文件最近一次 didChange/didOpen 的发送时刻（诊断新鲜度基线，见
    /// <see cref="NotifyFileUpdatedAsync"/>）。文件从未经过写通知路径时返回 null，
    /// 调用方（LspNotifier）回落到调用时刻作为基线。
    /// </summary>
    public DateTimeOffset? GetLastDidChangeUtc(string filePath) =>
        _lastDidChangeUtc.TryGetValue(LspUriHelper.BuildFileUri(filePath), out var t) ? t : null;

    /// <summary>
    /// Notify all servers that a directory (and everything under it) was deleted:
    /// closes every tracked document under the directory so servers stop publishing
    /// diagnostics for paths that no longer exist. 匹配的大小写语义随平台：Windows
    /// 路径大小写不敏感（OrdinalIgnoreCase），Unix 严格（Ordinal）——同
    /// <c>DeleteTool.IsWorkspaceRoot</c> 先例。Unix 上欠匹配是安全方向：漏发的
    /// didClose 由诊断周期清理（CleanupExpired）兜底；过匹配反而会误关仍打开的文档。
    /// 前缀匹配带分隔符边界（never matches siblings such as <c>/a/bc</c> when
    /// deleting <c>/a/b</c>）。
    /// </summary>
    public async Task NotifyDirectoryDeletedAsync(string directoryPath, CancellationToken ct = default)
    {
        var normalizedDir = directoryPath.Replace('/', Path.DirectorySeparatorChar)
            .TrimEnd(Path.DirectorySeparatorChar);
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        var deletedUris = _fileVersions.Keys
            .Where(uri => IsUnderDirectory(LspUriHelper.UriToFilePath(uri), normalizedDir, comparison))
            .ToList();

        foreach (var uri in deletedUris)
            await NotifyFileClosedAsync(LspUriHelper.UriToFilePath(uri), ct).ConfigureAwait(false);

        if (deletedUris.Count > 0)
            _logger.LogDebug("Closed {Count} LSP documents under deleted directory {Path}", deletedUris.Count, directoryPath);
    }

    /// <summary>目录归属判定（internal 供单测）：带分隔符边界的前缀匹配，/a/b 不匹配 /a/bc。</summary>
    internal static bool IsUnderDirectory(string filePath, string directory, StringComparison comparison)
    {
        var prefix = directory.Replace('/', Path.DirectorySeparatorChar)
            .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return filePath.Replace('/', Path.DirectorySeparatorChar).StartsWith(prefix, comparison);
    }

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
