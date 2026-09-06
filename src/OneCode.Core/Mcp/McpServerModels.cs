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
    int? StartupTimeoutMs = null,
    // 工具白名单（支持 * 通配符）。null = 未配置，暴露全部工具；
    // 空列表 = 连接服务器但不暴露任何工具。匹配规则见 McpToolFilter。
    IReadOnlyList<string>? EnabledTools = null)
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
        // In-process servers are resolved by name through InProcessMcpServerRegistry
        // (IInProcessMcpServerProvider implementations); a non-empty identifier
        // (Url or Command slot) is sufficient.
        McpTransportType.InProcess => !string.IsNullOrWhiteSpace(Url) || !string.IsNullOrWhiteSpace(Command),
        _ => false
    };
}

/// <summary>
/// Runtime status snapshot of one MCP server connection (name, definition, connection
/// state, tool count). Used by <c>/mcp list</c> and <c>/doctor</c> to show runtime state.
/// <see cref="LastError"/> carries the most recent connection failure reason
/// (null when connected or never attempted) — surfaced in <c>/mcp list</c> so a
/// broken server is never a silent "[disconnected]".
/// </summary>
public sealed record McpServerStatus(
    string Name,
    McpServerDefinition Definition,
    bool IsConnected,
    int ToolCount,
    string? LastError = null);

/// <summary>
/// 连接池三态快照：状态栏「连接中 x/y / 已连 n / 失败 k」与欢迎页 MCP 诊断行的数据源。
/// </summary>
/// <param name="Expected">最近一次启动连接的启用服务器目标数（预连接未启动过为 0）。</param>
/// <param name="Connected">已连接且工具可用的服务器数。</param>
/// <param name="Connecting">正在握手中的连接数（含按需/自动重连）。</param>
/// <param name="Failed">连接失败/掉线的服务器数（软失败条目，失败原因见 /mcp list）。</param>
/// <param name="ToolCount">已连接服务器的工具总数。</param>
public sealed record McpConnectionSummary(
    int Expected,
    int Connected,
    int Connecting,
    int Failed,
    int ToolCount)
{
    /// <summary>是否存在任何 MCP 活动——决定状态栏/欢迎页是否渲染该指示（零活动时隐藏降噪）。</summary>
    public bool HasActivity => Expected > 0 || Connected > 0 || Connecting > 0 || Failed > 0;
}
