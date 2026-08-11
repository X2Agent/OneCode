namespace OneCode.Infrastructure.Mcp;

/// <summary>
/// MCP configuration parser.
/// Parses mcp.json,
/// supports stdio/sse/http transports with environment variable expansion.
/// </summary>
public static class McpConfigParser
{
    /// <summary>
    /// Parse MCP server configuration from JSON string.
    /// </summary>
    public static ParsedMcpServers Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var mcpServers = root.TryGetProperty("mcpServers", out var serversProp)
            ? serversProp
            : root;

        Dictionary<string, McpServerDefinition> configs = [];

        foreach (var prop in mcpServers.EnumerateObject())
        {
            var serverDef = ParseServerDefinition(prop.Value);
            configs[prop.Name] = serverDef;
        }

        return new ParsedMcpServers(configs);
    }

    /// <summary>
    /// Parse MCP server configuration from file.
    /// </summary>
    public static async Task<ParsedMcpServers> ParseFromFileAsync(
        string path,
        CancellationToken ct = default)
    {
        var json = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
        return Parse(json);
    }

    private static McpServerDefinition ParseServerDefinition(JsonElement element)
    {
        string? type = null;
        string? command = null;
        string[] args = Array.Empty<string>();
        string? url = null;
        Dictionary<string, string>? env = null;
        Dictionary<string, string>? headers = null;
        bool disabled = false;
        int? initTimeoutMs = null;
        int? startupTimeoutMs = null;

        if (element.TryGetProperty("type", out var typeEl))
            type = typeEl.GetString();

        if (element.TryGetProperty("command", out var cmdEl))
            command = cmdEl.GetString();

        if (element.TryGetProperty("args", out var argsEl) && argsEl.ValueKind == JsonValueKind.Array)
        {
            args = argsEl.EnumerateArray()
                .Select(e => e.GetString() ?? "")
                .ToArray();
        }

        if (element.TryGetProperty("url", out var urlEl))
            url = urlEl.GetString();

        if (element.TryGetProperty("env", out var envEl) && envEl.ValueKind == JsonValueKind.Object)
        {
            env = [];
            foreach (var prop in envEl.EnumerateObject())
            {
                env[prop.Name] = prop.Value.GetString() ?? "";
            }
        }

        if (element.TryGetProperty("headers", out var headersEl) && headersEl.ValueKind == JsonValueKind.Object)
        {
            headers = [];
            foreach (var prop in headersEl.EnumerateObject())
            {
                headers[prop.Name] = prop.Value.GetString() ?? "";
            }
        }

        if (element.TryGetProperty("disabled", out var disabledEl))
            disabled = disabledEl.GetBoolean();

        if (element.TryGetProperty("initTimeoutMs", out var initTimeoutEl))
            initTimeoutMs = initTimeoutEl.GetInt32();

        if (element.TryGetProperty("startupTimeoutMs", out var startupTimeoutEl))
            startupTimeoutMs = startupTimeoutEl.GetInt32();

        // Determine transport type
        var transportType = type?.ToLowerInvariant() switch
        {
            "sse" => McpTransportType.Sse,
            "http" => McpTransportType.Http,
            "stdio" => McpTransportType.Stdio,
            "ws" or "websocket" => McpTransportType.WebSocket,
            "inprocess" or "in-process" => McpTransportType.InProcess,
            _ => command != null ? McpTransportType.Stdio
                : url != null && (url.StartsWith("ws://", StringComparison.OrdinalIgnoreCase)
                                  || url.StartsWith("wss://", StringComparison.OrdinalIgnoreCase))
                    ? McpTransportType.WebSocket
                    : url != null ? McpTransportType.Sse
                    : McpTransportType.Stdio
        };

        return new McpServerDefinition(
            TransportType: transportType,
            Command: command,
            Args: args,
            Url: url,
            Env: env,
            Headers: headers,
            Disabled: disabled,
            InitTimeoutMs: initTimeoutMs,
            StartupTimeoutMs: startupTimeoutMs);
    }
}

/// <summary>
/// MCP server configuration container.
/// </summary>
public sealed record ParsedMcpServers(
    IReadOnlyDictionary<string, McpServerDefinition> Servers);

/// <summary>
/// MCP transport type.
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
        // (Url or Command slot) is sufficient — runtime factories in McpConnectionManager
        // provide the actual implementation.
        McpTransportType.InProcess => !string.IsNullOrWhiteSpace(Url) || !string.IsNullOrWhiteSpace(Command),
        _ => false
    };
}
