namespace OneCode.Core.Lsp;

/// <summary>
/// LSP server manager abstraction.
/// Implemented by OneCode.App.Services.Lsp.LspServerManager.
/// </summary>
public interface ILspServerManager
{
    Task<bool> StartServerAsync(LspServerConfig config, CancellationToken ct = default);
    Task<bool> StopServerAsync(string name, CancellationToken ct = default);
    /// <summary>
    /// 手动重启：stop + start（对齐 <c>/mcp connect</c> 的手动恢复语义）。
    /// 成功即清零崩溃自动重启计数与最近启动错误。
    /// </summary>
    Task<bool> RestartServerAsync(string name, CancellationToken ct = default);
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
    /// <summary>Server is pushing workDoneProgress (e.g. indexing a large solution) — ready-ness signal.</summary>
    public bool IsIndexing { get; init; }
    /// <summary>
    /// 最近一次启动失败的原因（对齐 MCP <c>McpServerConnection.LastError</c> 的软失败语义）：
    /// manager 在启动失败后不保留实例，此字段由 manager 单独落池，保证失败原因对 <c>/lsp status</c> 可见。
    /// </summary>
    public string? LastError { get; init; }
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
