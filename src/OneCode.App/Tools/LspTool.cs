using System.ComponentModel;
using OneCode.App.Services.Lsp;
using OneCode.Core.Lsp;
using OneCode.Infrastructure.Config;
using OneCode.Infrastructure.Text;

namespace OneCode.App.Tools;

/// <summary>
/// LSP tool — exposes Language Server Protocol operations to the agent.
/// Supports semantic code navigation (definition, declaration, typeDefinition, implementation,
/// references, hover, documentHighlight), diagnostics, completion, code actions (with resolve),
/// rename (with prepare), formatting, signature help, workspace command execution,
/// and workspace-wide symbol search.
/// Server routing is automatic by file extension when the server parameter is omitted.
/// </summary>
public sealed class LspTool
{
    private readonly ILspServerManager _serverManager;
    private readonly LanguagePackRegistry _packRegistry;
    private readonly ILogger<LspTool>? _logger;

    public LspTool(ILspServerManager serverManager, LanguagePackRegistry packRegistry, ILogger<LspTool>? logger = null)
    {
        _serverManager = serverManager;
        _packRegistry = packRegistry;
        _logger = logger;
    }

    [Description("Perform Language Server Protocol operations: definition, declaration, typeDefinition, implementation, references, hover, documentHighlight, diagnostics, symbols, completion, codeAction, codeActionResolve, rename, prepareRename, formatting, signatureHelp, executeCommand, workspaceSymbol.")]
    public async Task<ToolResult> ExecuteLspAsync(
        [Description("LSP action: definition, declaration, typeDefinition, implementation, references, hover, documentHighlight, diagnostics, symbols, completion, codeAction, codeActionResolve, rename, prepareRename, formatting, signatureHelp, executeCommand, workspaceSymbol")] string action,
        [Description("File path (required for all actions except workspaceSymbol, executeCommand, and hierarchy sub-actions)")] string file = "",
        [Description("Line number (1-based, required for position-based actions)")] int line = 1,
        [Description("Column number (1-based, required for position-based actions)")] int column = 1,
        [Description("LSP server name (optional, auto-resolved by file extension if omitted)")] string? server = null,
        [Description("New name for rename action")] string? newName = null,
        [Description("Query string for workspaceSymbol; JSON CodeAction for codeActionResolve; command name for executeCommand; JSON item for callHierarchy/typeHierarchy sub-actions")] string? query = null,
        [Description("JSON-serialized arguments array for executeCommand (optional)")] string? arguments = null,
        CancellationToken ct = default)
    {
        // workspaceSymbol is workspace-scoped: needs no file, requires a query string.
        if (action.ToLowerInvariant() == "workspacesymbol")
        {
            if (string.IsNullOrWhiteSpace(query))
                return ToolResult.Error("workspaceSymbol requires the 'query' parameter.");
            var wsResult = await ExecuteWorkspaceSymbolAsync(query, ct).ConfigureAwait(false);
            return ToolResult.JsonSuccess(wsResult);
        }

        // executeCommand is workspace-scoped: needs no file, requires a command name in 'query'.
        if (action.ToLowerInvariant() == "executecommand")
        {
            if (string.IsNullOrWhiteSpace(query))
                return ToolResult.Error("executeCommand requires the 'query' parameter (command name).");

            var ecStatus = _serverManager.GetStatus();
            if (ecStatus.Count == 0)
                return ToolResult.Error("No LSP servers running. Use /lsp install <lang> to set up a language server.");

            var ecServer = server ?? ecStatus.FirstOrDefault()?.Name;
            if (ecServer == null)
                return ToolResult.Error("No LSP server available. Use /lsp install <lang>.");

            var ecCaps = ecStatus.FirstOrDefault(s => s.Name == ecServer)?.Capabilities;
            var ecCapError = CheckCapability(ecCaps, "workspace/executeCommand", action, ecServer);
            if (ecCapError != null) return ecCapError;

            try
            {
                var ecResult = await ExecuteCommandAsync(ecServer, query, arguments, ct).ConfigureAwait(false);
                return ToolResult.JsonSuccess(ecResult);
            }
            catch (Exception ex)
            {
                return ToolResult.Error($"LSP {action} failed: {ex.Message}");
            }
        }

        if (string.IsNullOrWhiteSpace(file))
            return ToolResult.Error("Parameter 'file' is required for this action.");

        var status = _serverManager.GetStatus();
        if (status.Count == 0)
            return ToolResult.Error("No LSP servers running. Use /lsp install <lang> to set up a language server.");

        // Auto-resolve server by file extension when not specified
        var targetServer = server ?? _packRegistry.ResolveServerName(file) ?? status.FirstOrDefault()?.Name;
        if (targetServer == null)
            return ToolResult.Error("No LSP server available for this file type. Use /lsp install <lang>.");

        // Retrieve capabilities for the resolved server to gate unsupported methods
        var serverCapabilities = status.FirstOrDefault(s => s.Name == targetServer)?.Capabilities;

        // Check server capability before dispatching — avoids server errors for unsupported methods
        var lspMethod = ActionToLspMethod(action.ToLowerInvariant());
        if (lspMethod != null)
        {
            var capError = CheckCapability(serverCapabilities, lspMethod, action, targetServer);
            if (capError != null) return capError;
        }

        try
        {
            var uri = LspUriHelper.BuildFileUri(Path.GetFullPath(file));
            var result = action.ToLowerInvariant() switch
            {
                "definition" => await GetDefinitionAsync(targetServer, uri, line, column, ct).ConfigureAwait(false),
                "declaration" => await GetDeclarationAsync(targetServer, uri, line, column, ct).ConfigureAwait(false),
                "typedefinition" => await GetTypeDefinitionAsync(targetServer, uri, line, column, ct).ConfigureAwait(false),
                "implementation" => await GetImplementationAsync(targetServer, uri, line, column, ct).ConfigureAwait(false),
                "references" => await GetReferencesAsync(targetServer, uri, line, column, ct).ConfigureAwait(false),
                "hover" => await GetHoverAsync(targetServer, uri, line, column, ct).ConfigureAwait(false),
                "documenthighlight" => await GetDocumentHighlightAsync(targetServer, uri, line, column, ct).ConfigureAwait(false),
                "diagnostics" => GetDiagnostics(targetServer, uri),
                "symbols" => await GetDocumentSymbolsAsync(targetServer, uri, ct).ConfigureAwait(false),
                "completion" => await GetCompletionAsync(targetServer, uri, line, column, ct).ConfigureAwait(false),
                "codeaction" => await GetCodeActionsAsync(targetServer, uri, line, column, ct).ConfigureAwait(false),
                "codeactionresolve" => await ResolveCodeActionAsync(targetServer, query, ct).ConfigureAwait(false),
                "rename" => await RenameAsync(targetServer, uri, line, column, newName ?? "newName", ct).ConfigureAwait(false),
                "preparerename" => await PrepareRenameAsync(targetServer, uri, line, column, ct).ConfigureAwait(false),
                "formatting" => await FormatAsync(targetServer, uri, ct).ConfigureAwait(false),
                "signaturehelp" => await GetSignatureHelpAsync(targetServer, uri, line, column, ct).ConfigureAwait(false),
                _ => (object)new { error = $"Unknown LSP action: {action}. Supported: definition, declaration, typeDefinition, implementation, references, hover, documentHighlight, diagnostics, symbols, completion, codeAction, codeActionResolve, rename, prepareRename, formatting, signatureHelp, executeCommand, workspaceSymbol" }
            };

            return ToolResult.JsonSuccess(result);
        }
        catch (Exception ex)
        {
            return ToolResult.Error($"LSP {action} failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Execute workspace/symbol on every running server and merge results.
    /// Returns a unified list of SymbolInformation objects across all servers,
    /// which lets the agent find symbols project-wide without knowing which
    /// language server owns them.
    /// </summary>
    private async Task<object> ExecuteWorkspaceSymbolAsync(string query, CancellationToken ct)
    {
        var status = _serverManager.GetStatus();
        if (status.Count == 0)
            return new { symbols = Array.Empty<object>() };

        var @params = JsonSerializer.SerializeToElement(new { query });
        var merged = new List<JsonElement>();
        var errors = new List<string>();

        foreach (var s in status)
        {
            if (!s.IsInitialized) continue;
            try
            {
                var result = await _serverManager.SendRequestAsync(s.Name, "workspace/symbol", @params, ct).ConfigureAwait(false);
                if (result is { } el && el.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in el.EnumerateArray())
                        merged.Add(item.Clone());
                }
            }
            catch (Exception ex)
            {
                // One server failing should not abort the whole query — collect and continue.
                errors.Add($"{s.Name}: {ex.Message}");
            }
        }

        return new { symbols = merged, errors = errors.Count == 0 ? null : errors };
    }

    private async Task<object> GetDefinitionAsync(string server, string uri, int line, int column, CancellationToken ct)
    {
        var @params = new { textDocument = new { uri }, position = new { line = line - 1, character = column - 1 } };
        var result = await _serverManager.SendRequestAsync(server, "textDocument/definition", JsonSerializer.SerializeToElement(@params), ct).ConfigureAwait(false);
        return (object?)result ?? new { locations = Array.Empty<object>() };
    }

    private async Task<object> GetTypeDefinitionAsync(string server, string uri, int line, int column, CancellationToken ct)
    {
        var @params = new { textDocument = new { uri }, position = new { line = line - 1, character = column - 1 } };
        var result = await _serverManager.SendRequestAsync(server, "textDocument/typeDefinition", JsonSerializer.SerializeToElement(@params), ct).ConfigureAwait(false);
        return (object?)result ?? new { locations = Array.Empty<object>() };
    }

    private async Task<object> GetImplementationAsync(string server, string uri, int line, int column, CancellationToken ct)
    {
        var @params = new { textDocument = new { uri }, position = new { line = line - 1, character = column - 1 } };
        var result = await _serverManager.SendRequestAsync(server, "textDocument/implementation", JsonSerializer.SerializeToElement(@params), ct).ConfigureAwait(false);
        return (object?)result ?? new { locations = Array.Empty<object>() };
    }

    private async Task<object> GetReferencesAsync(string server, string uri, int line, int column, CancellationToken ct)
    {
        var @params = new { textDocument = new { uri }, position = new { line = line - 1, character = column - 1 }, context = new { includeDeclaration = true } };
        var result = await _serverManager.SendRequestAsync(server, "textDocument/references", JsonSerializer.SerializeToElement(@params), ct).ConfigureAwait(false);
        return (object?)result ?? new { references = Array.Empty<object>() };
    }

    private async Task<object> GetHoverAsync(string server, string uri, int line, int column, CancellationToken ct)
    {
        var @params = new { textDocument = new { uri }, position = new { line = line - 1, character = column - 1 } };
        var result = await _serverManager.SendRequestAsync(server, "textDocument/hover", JsonSerializer.SerializeToElement(@params), ct).ConfigureAwait(false);
        return (object?)result ?? new { contents = "" };
    }

    private object GetDiagnostics(string server, string uri)
    {
        var diagnostics = _serverManager.GetDiagnostics(server);
        return new
        {
            server,
            uri,
            diagnostics = diagnostics.Select(d => new { d.Severity, d.Message, d.File, d.Line, d.Column }).ToArray()
        };
    }

    private async Task<object> GetDocumentSymbolsAsync(string server, string uri, CancellationToken ct)
    {
        var @params = new { textDocument = new { uri } };
        var result = await _serverManager.SendRequestAsync(server, "textDocument/documentSymbol", JsonSerializer.SerializeToElement(@params), ct).ConfigureAwait(false);
        return (object?)result ?? new { symbols = Array.Empty<object>() };
    }

    private async Task<object> GetCompletionAsync(string server, string uri, int line, int column, CancellationToken ct)
    {
        var @params = new { textDocument = new { uri }, position = new { line = line - 1, character = column - 1 } };
        var result = await _serverManager.SendRequestAsync(server, "textDocument/completion", JsonSerializer.SerializeToElement(@params), ct).ConfigureAwait(false);
        return (object?)result ?? new { items = Array.Empty<object>() };
    }

    private async Task<object> GetCodeActionsAsync(string server, string uri, int line, int column, CancellationToken ct)
    {
        var @params = new
        {
            textDocument = new { uri },
            range = new { start = new { line = line - 1, character = column - 1 }, end = new { line = line - 1, character = column } },
            context = new { diagnostics = Array.Empty<object>() }
        };
        var result = await _serverManager.SendRequestAsync(server, "textDocument/codeAction", JsonSerializer.SerializeToElement(@params), ct).ConfigureAwait(false);
        return (object?)result ?? new { actions = Array.Empty<object>() };
    }

    /// <summary>
    /// Resolve a CodeAction to fill in its `edit` field. The agent passes back the
    /// CodeAction JSON returned from codeAction, and the server fills in the
    /// WorkspaceEdit that ApplyWorkspaceEditTool can apply.
    /// </summary>
    private async Task<object> ResolveCodeActionAsync(string server, string? codeActionJson, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(codeActionJson))
            return new { error = "codeActionResolve requires the 'query' parameter to be the JSON CodeAction returned by codeAction." };

        JsonElement codeAction;
        try
        {
            using var doc = JsonDocument.Parse(codeActionJson);
            codeAction = doc.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            return new { error = $"Invalid CodeAction JSON: {ex.Message}" };
        }

        var result = await _serverManager.SendRequestAsync(server, "codeAction/resolve", codeAction, ct).ConfigureAwait(false);
        return (object?)result ?? new { error = "Server returned null for codeAction/resolve." };
    }

    private async Task<object> PrepareRenameAsync(string server, string uri, int line, int column, CancellationToken ct)
    {
        var @params = new { textDocument = new { uri }, position = new { line = line - 1, character = column - 1 } };
        var result = await _serverManager.SendRequestAsync(server, "textDocument/prepareRename", JsonSerializer.SerializeToElement(@params), ct).ConfigureAwait(false);
        // null result means the position is not renamable — return that explicitly so the agent can decide.
        return (object?)result ?? new { renamable = false };
    }

    private async Task<object> RenameAsync(string server, string uri, int line, int column, string newName, CancellationToken ct)
    {
        var @params = new { textDocument = new { uri }, position = new { line = line - 1, character = column - 1 }, newName };
        var result = await _serverManager.SendRequestAsync(server, "textDocument/rename", JsonSerializer.SerializeToElement(@params), ct).ConfigureAwait(false);
        return (object?)result ?? new { changes = new { } };
    }

    private async Task<object> FormatAsync(string server, string uri, CancellationToken ct)
    {
        var @params = new { textDocument = new { uri }, options = new { tabSize = Constants.Lsp.DefaultTabSize, insertSpaces = true } };
        var result = await _serverManager.SendRequestAsync(server, "textDocument/formatting", JsonSerializer.SerializeToElement(@params), ct).ConfigureAwait(false);
        return (object?)result ?? new { edits = Array.Empty<object>() };
    }

    private async Task<object> GetSignatureHelpAsync(string server, string uri, int line, int column, CancellationToken ct)
    {
        var @params = new { textDocument = new { uri }, position = new { line = line - 1, character = column - 1 } };
        var result = await _serverManager.SendRequestAsync(server, "textDocument/signatureHelp", JsonSerializer.SerializeToElement(@params), ct).ConfigureAwait(false);
        return (object?)result ?? new { signatures = Array.Empty<object>() };
    }

    // Phase 3: declaration / documentHighlight

    private async Task<object> GetDeclarationAsync(string server, string uri, int line, int column, CancellationToken ct)
    {
        var @params = new { textDocument = new { uri }, position = new { line = line - 1, character = column - 1 } };
        var result = await _serverManager.SendRequestAsync(server, "textDocument/declaration", JsonSerializer.SerializeToElement(@params), ct).ConfigureAwait(false);
        return (object?)result ?? new { locations = Array.Empty<object>() };
    }

    private async Task<object> GetDocumentHighlightAsync(string server, string uri, int line, int column, CancellationToken ct)
    {
        var @params = new { textDocument = new { uri }, position = new { line = line - 1, character = column - 1 } };
        var result = await _serverManager.SendRequestAsync(server, "textDocument/documentHighlight", JsonSerializer.SerializeToElement(@params), ct).ConfigureAwait(false);
        return (object?)result ?? new { highlights = Array.Empty<object>() };
    }

    // Phase 3: workspace/executeCommand

    /// <summary>
    /// Execute a workspace command on the server. The command name comes from a
    /// CodeAction's <c>command.command</c> field, and arguments from
    /// <c>command.arguments</c>. Both are passed through as-is.
    /// </summary>
    private async Task<object> ExecuteCommandAsync(string server, string command, string? argumentsJson, CancellationToken ct)
    {
        JsonElement[]? args = null;
        if (!string.IsNullOrWhiteSpace(argumentsJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(argumentsJson);
                if (doc.RootElement.ValueKind == JsonValueKind.Array)
                {
                    args = doc.RootElement.EnumerateArray().Select(e => e.Clone()).ToArray();
                }
            }
            catch (JsonException ex)
            {
                // Malformed arguments — send empty array so the command still dispatches.
                if (_logger is not null)
                    _logger.LogDebug(ex, "LspTool: malformed arguments JSON for command {Command}, sending empty array", command);
                else
                    System.Diagnostics.Debug.WriteLine($"LspTool: malformed arguments JSON for command {command}: {ex.Message}");
            }
        }

        var @params = args is null
            ? JsonSerializer.SerializeToElement(new { command })
            : JsonSerializer.SerializeToElement(new { command, arguments = args });
        var result = await _serverManager.SendRequestAsync(server, "workspace/executeCommand", @params, ct).ConfigureAwait(false);
        return (object?)result ?? new { result = (object?)null };
    }

    /// <summary>
    /// Map an action name to the LSP method it dispatches, or null if the action
    /// doesn't require a server request (diagnostics, workspaceSymbol, unknown).
    /// Used for capability gating before dispatching.
    /// </summary>
    private static string? ActionToLspMethod(string action) => action switch
    {
        "definition" => "textDocument/definition",
        "typedefinition" => "textDocument/typeDefinition",
        "implementation" => "textDocument/implementation",
        "references" => "textDocument/references",
        "hover" => "textDocument/hover",
        "symbols" => "textDocument/documentSymbol",
        "completion" => "textDocument/completion",
        "codeaction" => "textDocument/codeAction",
        "codeactionresolve" => "codeAction/resolve",
        "rename" => "textDocument/rename",
        "preparerename" => "textDocument/prepareRename",
        "formatting" => "textDocument/formatting",
        "signaturehelp" => "textDocument/signatureHelp",
        "declaration" => "textDocument/declaration",
        "documenthighlight" => "textDocument/documentHighlight",
        "executecommand" => "workspace/executeCommand",
        _ => null,
    };

    /// <summary>
    /// Returns null if the server supports the method, or a ToolResult error if not.
    /// When capabilities are not yet advertised (null), the check is skipped —
    /// LspServerInstance.SupportsMethod returns false for null, but we treat
    /// "no capabilities advertised" as "allow and let the server reject" to avoid
    /// blocking actions on servers that don't advertise capabilities properly.
    /// </summary>
    private static ToolResult? CheckCapability(JsonElement? capabilities, string method, string action, string serverName)
    {
        // If capabilities haven't been advertised yet, allow the request — the server
        // will reject it if unsupported, and we get a cleaner error than pre-gating.
        if (capabilities is null)
            return null;

        if (!LspServerInstance.SupportsMethod(capabilities, method))
        {
            return ToolResult.Error(
                $"Server '{serverName}' does not support {action}. " +
                "Available capabilities may not include this method.");
        }
        return null;
    }
}
