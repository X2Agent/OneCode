using OneCode.Core.Lsp;
namespace OneCode.App.Services.Lsp;

/// <summary>
/// Wraps an LspClient with server-specific logic and state tracking.
/// </summary>
public sealed class LspServerInstance
{
    private readonly LspServerConfig _config;
    private readonly ILogger<LspServerInstance> _logger;
    private readonly ILogger<LspClient> _clientLogger;
    private readonly LspDiagnosticRegistry _diagnosticRegistry;
    private LspClient? _client;
    private bool _isHealthy = true;
    private DateTimeOffset? _lastActivity;

    public LspServerInstance(LspServerConfig config, ILoggerFactory loggerFactory, LspDiagnosticRegistry diagnosticRegistry)
    {
        _config = config;
        _logger = loggerFactory.CreateLogger<LspServerInstance>();
        _clientLogger = loggerFactory.CreateLogger<LspClient>();
        _diagnosticRegistry = diagnosticRegistry;
    }

    public LspServerConfig Config => _config;
    public bool IsRunning => _client != null;
    public bool IsInitialized => _client?.IsInitialized ?? false;
    public bool IsHealthy => _isHealthy;
    public JsonElement? Capabilities => _client?.Capabilities;
    public DateTimeOffset? LastActivity => _lastActivity;

    /// <summary>
    /// Check whether this server has declared support for the given LSP method
    /// based on its advertised capabilities. Returns true for built-in lifecycle
    /// methods ($/cancelRequest, initialize, etc.) and uses an optimistic default
    /// for unknown methods.
    /// </summary>
    public bool SupportsMethod(string method) => SupportsMethod(Capabilities, method);

    /// <summary>
    /// Static capability check usable without an instance (e.g. from LspTool
    /// which only has access to LspServerStatus.Capabilities).
    /// </summary>
    public static bool SupportsMethod(JsonElement? capabilities, string method)
    {
        if (capabilities is null)
            return false;

        var caps = capabilities.Value;

        // Built-in JSON-RPC / LSP lifecycle methods — always supported
        if (method.StartsWith('$') || method is "initialize" or "initialized" or "shutdown" or "exit")
            return true;

        var slashIndex = method.IndexOf('/');
        if (slashIndex < 0)
            return true;

        var category = method[..slashIndex];
        var rest = method[(slashIndex + 1)..];

        // Map method to capability path. Most follow: textDocument/definition → capabilities.textDocument.definition.
        // Special cases handle resolve providers and sub-methods (e.g. semanticTokens/full → semanticTokens).
        return (category, rest) switch
        {
            ("codeAction", "resolve") => HasPath(caps, "textDocument", "codeAction", "resolveProvider"),
            ("callHierarchy", _) => HasPath(caps, "textDocument", "callHierarchy"),
            ("typeHierarchy", _) => HasPath(caps, "textDocument", "typeHierarchy"),
            ("textDocument", var r) when r.StartsWith("semanticTokens", StringComparison.Ordinal) => HasPath(caps, "textDocument", "semanticTokens"),
            ("textDocument", "prepareRename") => HasPath(caps, "textDocument", "rename", "prepareSupport"),
            ("textDocument", "prepareCallHierarchy") => HasPath(caps, "textDocument", "callHierarchy"),
            ("textDocument", "prepareTypeHierarchy") => HasPath(caps, "textDocument", "typeHierarchy"),
            ("textDocument", "inlayHint") => HasPath(caps, "textDocument", "inlayHint"),
            _ => HasPath(caps, category, rest),
        };
    }

    private static bool HasPath(JsonElement caps, params string[] path)
    {
        var current = caps;
        foreach (var segment in path)
        {
            if (!current.TryGetProperty(segment, out current))
                return false;
        }
        return true;
    }

    public async Task StartAsync()
    {
        // Use injected logger — previous code used an empty LoggerFactory that silently discarded all logs
        _client = new LspClient(_config.Name, _clientLogger, OnServerCrash);

        await _client.StartAsync(
            _config.Command,
            _config.Args,
            _config.Environment,
            _config.WorkingDirectory
        ).ConfigureAwait(false);

        // Build initialize params as JSON
        var initParamsJson = BuildInitializeParamsJson();
        using var initDoc = JsonDocument.Parse(initParamsJson);
        await _client.InitializeAsync(initDoc.RootElement.Clone()).ConfigureAwait(false);

        // Register publishDiagnostics handler so server-pushed diagnostics are captured by the registry.
        // Without this, all diagnostics from the server are silently dropped.
        _client.OnNotification("textDocument/publishDiagnostics", _diagnosticRegistry.CreateHandler(_config.Name));

        _lastActivity = DateTimeOffset.UtcNow;
        _isHealthy = true;
    }

    public async Task<JsonElement?> SendRequestAsync(string method, JsonElement parameters, CancellationToken ct = default)
    {
        if (_client == null)
            throw new InvalidOperationException("Server not started");

        _lastActivity = DateTimeOffset.UtcNow;
        return await _client.SendRequestAsync(method, parameters, ct).ConfigureAwait(false);
    }

    public async Task SendNotificationAsync(string method, JsonElement parameters)
    {
        if (_client == null)
            throw new InvalidOperationException("Server not started");

        _lastActivity = DateTimeOffset.UtcNow;
        await _client.SendNotificationAsync(method, parameters).ConfigureAwait(false);
    }

    public async Task StopAsync()
    {
        if (_client != null)
        {
            await _client.StopAsync().ConfigureAwait(false);
            await _client.DisposeAsync().ConfigureAwait(false);
            _client = null;
        }
    }

    private string BuildInitializeParamsJson()
    {
        // LSP rootUri must be a properly-formed file:// URI. Naively concatenating
        // "file://" with a Windows path produces "file://C:\Users\..." whose backslashes
        // are not valid JSON escape characters — System.Text.Json rejects them with
        // "'U' is an invalid escapable character within a JSON string". Build the URI
        // via Uri.AbsoluteUri so backslashes become forward slashes and the drive
        // letter is correctly prefixed (file:///C:/Users/...).
        string? rootUri = null;
        string workspaceFoldersJson = "null";
        if (!string.IsNullOrEmpty(_config.WorkingDirectory))
        {
            rootUri = new Uri(_config.WorkingDirectory, UriKind.Absolute).AbsoluteUri;
            // LSP 3.x prefers workspaceFolders; servers like csharp-ls derive it from
            // rootUri when absent, but sending both is explicit and avoids CWD fallback.
            workspaceFoldersJson = $$"""[{"uri":"{{rootUri}}","name":"root"}]""";
        }
        var initOptions = _config.InitializationOptions?.GetRawText() ?? "null";

        return $$"""
        {
            "processId": {{Environment.ProcessId}},
            "clientInfo": { "name": "OneCode.NET", "version": "{{Core.Product.ProductInfo.Default.Version}}" },
            "rootUri": {{(rootUri != null ? $"\"{rootUri}\"" : "null")}},
            "workspaceFolders": {{workspaceFoldersJson}},
            "capabilities": {
                "workspace": {
                    "applyEdit": true,
                    "workspaceFolders": true,
                    "symbol": { "dynamicRegistration": true, "symbolKind": { "valueSet": [1,2,3,4,5,6,7,8,9,10,11,12,13,14,15,16,17,18,19,20,21,22,23,24,25,26] } },
                    "workspaceEdit": { "documentChanges": true, "resourceOperations": ["create", "rename", "delete"] },
                    "executeCommand": { "dynamicRegistration": false }
                },
                "textDocument": {
                    "synchronization": { "dynamicRegistration": true },
                    "completion": { "completionItem": { "snippetSupport": true } },
                    "hover": { "contentFormat": ["markdown", "plaintext"] },
                    "signatureHelp": { "signatureInformation": { "documentationFormat": ["markdown"] } },
                    "references": { "dynamicRegistration": true },
                    "definition": { "dynamicRegistration": true },
                    "declaration": { "dynamicRegistration": false },
                    "typeDefinition": { "dynamicRegistration": true },
                    "implementation": { "dynamicRegistration": true },
                    "documentHighlight": { "dynamicRegistration": false },
                    "codeAction": { "dynamicRegistration": true, "resolveSupport": { "properties": ["edit"] } },
                    "codeLens": { "dynamicRegistration": true },
                    "documentLink": { "dynamicRegistration": true },
                    "rename": { "dynamicRegistration": true, "prepareSupport": true },
                    "documentSymbol": { "hierarchicalDocumentSymbolSupport": true },
                    "formatting": { "dynamicRegistration": true },
                    "rangeFormatting": { "dynamicRegistration": true },
                    "callHierarchy": { "dynamicRegistration": false },
                    "typeHierarchy": { "dynamicRegistration": false },
                    "semanticTokens": { "dynamicRegistration": false, "requests": { "range": true, "full": { "delta": true } }, "tokenTypes": [], "tokenModifiers": [], "formats": ["relative"] },
                    "inlayHint": { "dynamicRegistration": false },
                    "publishDiagnostics": { "relatedInformation": true }
                },
                "window": { "workDoneProgress": true }
            },
            "initializationOptions": {{initOptions}}
        }
        """;
    }

    private void OnServerCrash(Exception ex)
    {
        _isHealthy = false;
        _logger.LogError(ex, "LSP server {ServerName} crashed", _config.Name);
    }
}
