namespace OneCode.Core.Lsp;

/// <summary>
/// LSP server manager abstraction.
/// Implemented by OneCode.App.Services.Lsp.LspServerManager.
/// </summary>
public interface ILspServerManager
{
    Task<bool> StartServerAsync(LspServerConfig config, CancellationToken ct = default);
    Task<bool> StopServerAsync(string name, CancellationToken ct = default);
    Task<JsonElement?> SendRequestAsync(string serverName, string method, JsonElement parameters, CancellationToken ct = default);
    IReadOnlyList<LspServerStatus> GetStatus();
    IReadOnlyList<LspDiagnosticEntry> GetDiagnostics(string? serverName = null);
}

/// <summary>
/// LSP server configuration.
/// </summary>
public sealed record LspServerConfig(
    string Name,
    string Command,
    string[] Args,
    Dictionary<string, string>? Environment = null,
    string? WorkingDirectory = null,
    JsonElement? InitializationOptions = null);

/// <summary>
/// Status of an LSP server.
/// </summary>
public sealed record LspServerStatus
{
    public required string Name { get; init; }
    public bool IsRunning { get; init; }
    public bool IsInitialized { get; init; }
    public JsonElement? Capabilities { get; init; }
}

/// <summary>
/// Diagnostic entry from an LSP server.
/// </summary>
public sealed record LspDiagnosticEntry
{
    public required string ServerName { get; init; }
    public required LspDiagnosticSeverity Severity { get; init; }
    public required string Message { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
    public string? File { get; init; }
    public int? Line { get; init; }
    public int? Column { get; init; }
}

/// <summary>
/// Severity level for LSP diagnostics.
/// </summary>
public enum LspDiagnosticSeverity
{
    Error = 1,
    Warning = 2,
    Information = 3,
    Hint = 4
}
