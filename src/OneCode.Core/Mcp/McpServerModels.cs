namespace OneCode.Core.Mcp;

/// <summary>
/// MCP transport types supported for server connections.
/// </summary>
public enum McpTransportType
{
    Stdio,
    Sse,
    Http,
    /// <summary>WebSocket transport (ws:// or wss:// URLs).</summary>
    WebSocket,
    /// <summary>In-process transport for same-process MCP servers (testing / embedding).</summary>
    InProcess,
}

/// <summary>
/// Definition of a single MCP server.
/// </summary>
public sealed record McpServerDefinition(
    McpTransportType TransportType,
    string? Command = null,
    IReadOnlyList<string>? Args = null,
    string? Url = null,
    IReadOnlyDictionary<string, string>? Env = null,
    IReadOnlyDictionary<string, string>? Headers = null,
    bool Disabled = false,
    int? InitTimeoutMs = null,
    int? StartupTimeoutMs = null)
{
    /// <summary>
    /// Whether this server has a valid configuration.
    /// </summary>
    public bool IsValid => TransportType switch
    {
        McpTransportType.Stdio => !string.IsNullOrWhiteSpace(Command),
        McpTransportType.Sse or McpTransportType.Http => !string.IsNullOrWhiteSpace(Url),
        // WebSocket also requires a URL (ws:// or wss://) — validated by the transport
        // type inference in the parser (URL prefix detection).
        McpTransportType.WebSocket => !string.IsNullOrWhiteSpace(Url),
        // In-process servers are registered programmatically; a non-empty identifier
        // (Url or Command slot) is sufficient. Note: InProcess transport has no runtime
        // connection branch yet in McpConnectionManager (falls through to the default
        // warning) — reserved for future same-process embedding.
        McpTransportType.InProcess => !string.IsNullOrWhiteSpace(Url) || !string.IsNullOrWhiteSpace(Command),
        _ => false
    };
}

/// <summary>
/// Runtime status snapshot of one MCP server connection (name, definition, connection
/// state, tool count). Used by <c>/mcp list</c> and <c>/doctor</c> to show runtime state.
/// </summary>
public sealed record McpServerStatus(
    string Name,
    McpServerDefinition Definition,
    bool IsConnected,
    int ToolCount);
