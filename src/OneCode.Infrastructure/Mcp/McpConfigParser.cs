using OneCode.Core.Mcp;

namespace OneCode.Infrastructure.Mcp;

/// <summary>
/// MCP configuration parser.
/// Parses mcp.json,
/// supports stdio/sse/http/ws transports with environment variable expansion
/// (see <see cref="ExpandEnvironmentVariables"/>).
/// </summary>
public static partial class McpConfigParser
{
    /// <summary>
    /// Expand <c>${VAR}</c> (and the Cursor-style <c>${env:VAR}</c> form) in config string
    /// values against the current process environment. Unresolvable variables expand to the
    /// empty string so that a missing secret fails connection instead of leaking the literal
    /// <c>${...}</c> into command args/headers.
    /// </summary>
    /// <remarks>
    /// Applied to <c>command</c>, every <c>args</c> element, <c>env</c> values, <c>url</c>
    /// and <c>headers</c> values — the fields where credentials/paths are conventionally
    /// parameterized (e.g. <c>${GITHUB_TOKEN}</c>, <c>${env:API_KEY}</c>).
    /// </remarks>
    internal static string ExpandEnvironmentVariables(string value) => EnvVariableRegex().Replace(
        value,
        match =>
        {
            var name = match.Groups[1].Value;
            return Environment.GetEnvironmentVariable(name) ?? string.Empty;
        });

    /// <summary>Matches ${VAR} / ${env:VAR} (VAR: letter/digit/underscore, not empty).</summary>
    [GeneratedRegex(@"\$\{(?:env:)?([A-Za-z_][A-Za-z0-9_]*)\}", RegexOptions.CultureInvariant)]
    private static partial Regex EnvVariableRegex();

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
        string[] args = [];
        string? url = null;
        Dictionary<string, string>? env = null;
        Dictionary<string, string>? headers = null;
        bool disabled = false;
        int? initTimeoutMs = null;
        int? startupTimeoutMs = null;
        List<string>? enabledTools = null;

        if (element.TryGetProperty("type", out var typeEl))
            type = typeEl.GetString();

        if (element.TryGetProperty("command", out var cmdEl))
        {
            // 保留 null 语义：transport 推断依赖 command == null 判定
            var rawCommand = cmdEl.GetString();
            command = rawCommand is null ? null : ExpandEnvironmentVariables(rawCommand);
        }

        if (element.TryGetProperty("args", out var argsEl) && argsEl.ValueKind == JsonValueKind.Array)
        {
            args = argsEl.EnumerateArray()
                .Select(e => ExpandEnvironmentVariables(e.GetString() ?? ""))
                .ToArray();
        }

        if (element.TryGetProperty("url", out var urlEl))
        {
            // 同 command：url == null 参与 transport 推断，不能折叠成空串
            var rawUrl = urlEl.GetString();
            url = rawUrl is null ? null : ExpandEnvironmentVariables(rawUrl);
        }

        if (element.TryGetProperty("env", out var envEl) && envEl.ValueKind == JsonValueKind.Object)
        {
            env = [];
            foreach (var prop in envEl.EnumerateObject())
            {
                env[prop.Name] = ExpandEnvironmentVariables(prop.Value.GetString() ?? "");
            }
        }

        if (element.TryGetProperty("headers", out var headersEl) && headersEl.ValueKind == JsonValueKind.Object)
        {
            headers = [];
            foreach (var prop in headersEl.EnumerateObject())
            {
                headers[prop.Name] = ExpandEnvironmentVariables(prop.Value.GetString() ?? "");
            }
        }

        if (element.TryGetProperty("disabled", out var disabledEl))
            disabled = disabledEl.GetBoolean();

        if (element.TryGetProperty("initTimeoutMs", out var initTimeoutEl))
            initTimeoutMs = initTimeoutEl.GetInt32();

        if (element.TryGetProperty("startupTimeoutMs", out var startupTimeoutEl))
            startupTimeoutMs = startupTimeoutEl.GetInt32();

        if (element.TryGetProperty("tools", out var toolsEl) && toolsEl.ValueKind == JsonValueKind.Array)
        {
            // 工具白名单：null = 全部暴露；空数组 = 不暴露任何工具（保留 resources 用途）。
            // 条目支持 * 通配符（匹配规则见 McpToolFilter），空白条目忽略。
            enabledTools = [];
            foreach (var toolEl in toolsEl.EnumerateArray())
            {
                var toolName = toolEl.GetString();
                if (!string.IsNullOrWhiteSpace(toolName))
                    enabledTools.Add(toolName);
            }
        }

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
            StartupTimeoutMs: startupTimeoutMs,
            EnabledTools: enabledTools);
    }
}

/// <summary>
/// MCP server configuration container.
/// </summary>
public sealed record ParsedMcpServers(
    IReadOnlyDictionary<string, McpServerDefinition> Servers);
