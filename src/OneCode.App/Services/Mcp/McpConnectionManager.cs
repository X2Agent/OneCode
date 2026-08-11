using Microsoft.Agents.AI.Mcp;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using OneCode.Infrastructure.Mcp;

namespace OneCode.App.Services.Mcp;

/// <summary>
/// Manages MCP server connection lifecycle and exposes their tools to QueryEngine.
/// Config is loaded from multi-scope .mcp.json files (user, project, local) via
/// <see cref="McpMultiScopeConfigLoader"/>.
/// </summary>
public sealed class McpConnectionManager : IMcpConnectionManager, IAsyncDisposable
{
    private readonly ILogger<McpConnectionManager> _logger;
    private readonly McpMultiScopeConfigLoader _multiScopeLoader;
    private readonly McpElicitationHandler _elicitationHandler;
    private readonly ConcurrentDictionary<string, McpServerConnection> _connections = new(StringComparer.Ordinal);
    private bool _disposed;

    public McpConnectionManager(
        McpMultiScopeConfigLoader multiScopeLoader,
        McpElicitationHandler elicitationHandler,
        ILogger<McpConnectionManager>? logger = null)
    {
        _logger = logger ?? NullLogger<McpConnectionManager>.Instance;
        _multiScopeLoader = multiScopeLoader;
        _elicitationHandler = elicitationHandler;
    }

    // Events

    /// <summary>
    /// Fires whenever the set of connected MCP servers changes (connect or disconnect).
    /// Subscribers should re-run <see cref="Commands.McpCommandSource.LoadCommandsAsync"/> to
    /// pick up new or removed /mcp:{server} slash commands.
    /// </summary>
    public event Action? ServersChanged;

    // Connect / Disconnect

    /// <summary>Default timeout per MCP server connection (30 seconds).</summary>
    private static readonly TimeSpan ConnectionTimeout = TimeSpan.FromSeconds(30);

    public async Task ConnectAllAsync(CancellationToken ct = default)
    {
        var merged = await _multiScopeLoader.LoadAllAsync(ct: ct).ConfigureAwait(false);
        var enabled = merged.Servers.Where(kv => !kv.Value.Disabled).ToDictionary(kv => kv.Key, kv => kv.Value);
        var disabledCount = merged.Servers.Count(kv => kv.Value.Disabled);

        if (disabledCount > 0)
            _logger.LogInformation("Skipping {Count} disabled MCP server(s).", disabledCount);

        if (enabled.Count == 0)
        {
            _logger.LogDebug("No MCP servers configured.");
            return;
        }

        _logger.LogInformation("Connecting to {Count} MCP server(s)...", enabled.Count);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(ConnectionTimeout);

        await Task.WhenAll(enabled.Select(kv => ConnectOneWithTimeoutAsync(kv.Key, kv.Value, timeoutCts.Token)));
    }

    private async Task ConnectOneWithTimeoutAsync(string name, McpServerDefinition def, CancellationToken ct)
    {
        try
        {
            await ConnectOneAsync(name, def, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("MCP server '{Name}' connection timed out after {Timeout}", name, ConnectionTimeout);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to connect to MCP server '{Name}'", name);
        }
    }

    /// <summary>
    /// Hot-connect a single server by name, loading its definition from the
    /// multi-scope config files. Returns false if the name is not configured or
    /// the connection failed. Safe to call repeatedly (reconnects).
    /// </summary>
    public async Task<bool> ConnectOneAsync(string name, CancellationToken ct = default)
    {
        var merged = await _multiScopeLoader.LoadAllAsync(ct: ct).ConfigureAwait(false);

        if (!merged.Servers.TryGetValue(name, out var def))
            return false;

        if (def.Disabled)
        {
            _logger.LogInformation("MCP server '{Name}' is disabled — skipping connection.", name);
            return false;
        }

        // Drop any stale connection first so the inner ConnectOneAsync below
        // isn't short-circuited by its own "already connected" early-return.
        if (_connections.TryRemove(name, out var prior))
        {
            try { await prior.Client.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogWarning(ex, "Error disposing prior MCP client for '{Name}'", name); }
        }

        await ConnectOneAsync(name, def, ct).ConfigureAwait(false);
        return _connections.TryGetValue(name, out var updated) && updated.IsConnected;
    }

    public async Task ConnectOneAsync(string name, McpServerDefinition def, CancellationToken ct = default)
    {
        if (_connections.ContainsKey(name))
            return;

        var clientLogger = NullLogger<McpClient>.Instance;
        var client = new McpClient(clientLogger, _elicitationHandler);
        var conn = new McpServerConnection(name, def, client);

        try
        {
            switch (def.TransportType)
            {
                case McpTransportType.Stdio:
                    await client.ConnectStdioAsync(def.Command ?? "", def.Args ?? [], def.Env, ct);
                    var stdioTools = await LoadAgentToolsAsync(client, name, ct).ConfigureAwait(false);
                    conn = conn with { AgentTools = stdioTools, IsConnected = true };
                    _logger.LogInformation("MCP server '{Name}' connected ({Count} tools)", name, stdioTools.Count);
                    break;

                case McpTransportType.Sse:
                    await client.ConnectSseAsync(def.Url!, def.Headers, ct);
                    var sseTools = await LoadAgentToolsAsync(client, name, ct).ConfigureAwait(false);
                    conn = conn with { AgentTools = sseTools, IsConnected = true };
                    _logger.LogInformation("MCP server '{Name}' connected via SSE ({Count} tools)", name, sseTools.Count);
                    break;

                case McpTransportType.Http:
                    await client.ConnectHttpAsync(def.Url!, def.Headers, ct);
                    var httpTools = await LoadAgentToolsAsync(client, name, ct).ConfigureAwait(false);
                    conn = conn with { AgentTools = httpTools, IsConnected = true };
                    _logger.LogInformation("MCP server '{Name}' connected via HTTP ({Count} tools)", name, httpTools.Count);
                    break;

                case McpTransportType.WebSocket:
                    if (string.IsNullOrEmpty(def.Url))
                    {
                        _logger.LogWarning("MCP WebSocket transport requires a URL -- skipping '{Name}'", name);
                        break;
                    }
                    var wsTransport = new WebSocketClientTransport(def.Url, _logger as ILogger<WebSocketClientTransport>);
                    await client.ConnectAsync(wsTransport, ct);
                    var wsTools = await LoadAgentToolsAsync(client, name, ct).ConfigureAwait(false);
                    conn = conn with { AgentTools = wsTools, IsConnected = true };
                    _logger.LogInformation("MCP server '{Name}' connected via WebSocket ({Count} tools)", name, wsTools.Count);
                    break;

                default:
                    _logger.LogWarning("MCP server '{Name}' unrecognized transport type '{Type}' -- skipping", name, def.TransportType);
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to connect MCP server '{Name}'", name);
        }

        _connections[name] = conn;
        if (conn.IsConnected)
            ServersChanged?.Invoke();
    }

    public async Task DisconnectAsync(string name)
    {
        if (_connections.TryRemove(name, out var conn))
        {
            await conn.Client.DisposeAsync();
            ServersChanged?.Invoke();
        }
    }

    public async Task ReconnectServerAsync(string name, CancellationToken ct = default)
    {
        if (_connections.TryRemove(name, out var existing))
            await existing.Client.DisposeAsync();

        var merged = await _multiScopeLoader.LoadAllAsync(ct: ct).ConfigureAwait(false);

        if (!merged.Servers.TryGetValue(name, out var def) || def.Disabled)
        {
            _logger.LogInformation("MCP server '{Name}' is not configured or disabled — skipping reconnection.", name);
            return;
        }

        await ConnectOneAsync(name, def, ct);
    }

    public McpServerDefinition? GetServerDefinition(string name)
    {
        return _connections.TryGetValue(name, out var conn) ? conn.Definition : null;
    }

    public IReadOnlyList<string> GetServerNames()
        => _connections.Keys.ToList();

    /// <summary>已连接且可用的 MCP 客户端（供技能/工具 MAF 集成使用）。</summary>
    public IReadOnlyList<(string Name, McpClient Client)> GetConnectedClients()
        => _connections.Values
            .Where(c => c.IsConnected)
            .Select(c => (c.Name, c.Client))
            .ToList();

    /// <summary>Lookup a connected client by server name (null if not connected).</summary>
    public McpClient? GetClient(string name)
        => _connections.TryGetValue(name, out var c) && c.IsConnected ? c.Client : null;

    // Tools

    /// <summary>
    /// Get all live tools from connected MCP servers as <see cref="AIFunction"/> adapters.
    /// Safe to call multiple times; returns a fresh snapshot each time.
    /// </summary>
    public IReadOnlyList<AIFunction> GetAllTools()
        => _connections.Values
            .Where(c => c.IsConnected)
            .SelectMany(c => c.AgentTools)
            .ToList();

    public async Task<IReadOnlyList<(string ServerName, McpPrompt Prompt)>> GetAllPromptsAsync(
        CancellationToken ct = default)
    {
        List<(string, McpPrompt)> results = [];
        foreach (var conn in _connections.Values.Where(c => c.IsConnected))
        {
            try
            {
                var prompts = await conn.Client.ListPromptsAsync(ct).ConfigureAwait(false);
                foreach (var p in prompts)
                    results.Add((conn.Name, p));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to list prompts from MCP server: {Server}", conn.Name);
            }
        }
        return results;
    }

    public IReadOnlyList<(string Name, McpServerDefinition Definition)> GetServerDefinitions()
    {
        List<(string, McpServerDefinition)> list = [];
        foreach (var (name, conn) in _connections)
        {
            if (conn.Definition.TransportType != McpTransportType.Stdio)
            {
                list.Add((name, conn.Definition));
            }
        }
        return list;
    }

    public IReadOnlyList<McpServerStatus> GetStatus()
        => _connections.Values
            .Select(c => new McpServerStatus(c.Name, c.Definition, c.IsConnected, c.AgentTools.Count))
            .ToList();

    // Helpers

    private const int MaxFunctionNameLength = 64;

    /// <summary>
    /// 通过 MAF <see cref="McpClientTaskExtensions.ListAgentToolsWithTaskSupportAsync"/> 加载工具，
    /// 并为每个工具名添加模型兼容的 <c>mcp__{server}__{tool}</c> 前缀。
    /// </summary>
    private static async Task<IReadOnlyList<AIFunction>> LoadAgentToolsAsync(
        McpClient client,
        string serverName,
        CancellationToken ct)
    {
        var sdkClient = client.SdkClient;
        if (sdkClient is null)
            return [];

        var tools = await sdkClient.ListAgentToolsWithTaskSupportAsync(cancellationToken: ct)
            .ConfigureAwait(false);

        var prefixed = new List<AIFunction>(tools.Count);
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var tool in tools)
        {
            var prefixedName = CreateUniqueToolName(serverName, tool.Name, usedNames);

            // McpClientTool has a public WithName() that returns a renamed copy.
            // TaskAwareMcpClientAIFunction (MAF internal) does not, so we wrap it.
            if (tool is ModelContextProtocol.Client.McpClientTool mcpTool)
                prefixed.Add(mcpTool.WithName(prefixedName));
            else
                prefixed.Add(new RenamedAIFunction(tool, prefixedName));
        }

        return prefixed;
    }

    internal static string CreateUniqueToolName(
        string serverName, string toolName, ISet<string> usedNames)
    {
        ArgumentNullException.ThrowIfNull(usedNames);
        var server = NormalizeNameSegment(serverName, "server");
        var tool = NormalizeNameSegment(toolName, "tool");
        var prefix = $"mcp__{server}__";
        var available = Math.Max(1, MaxFunctionNameLength - prefix.Length);
        var baseName = prefix + tool[..Math.Min(tool.Length, available)];
        if (usedNames.Add(baseName)) return baseName;

        for (var suffix = 2; ; suffix++)
        {
            var suffixText = $"__{suffix}";
            var bodyLength = Math.Max(1, MaxFunctionNameLength - prefix.Length - suffixText.Length);
            var candidate = prefix + tool[..Math.Min(tool.Length, bodyLength)] + suffixText;
            if (usedNames.Add(candidate)) return candidate;
        }
    }

    private static string NormalizeNameSegment(string? value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        var chars = value.Select(static c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-'
                ? c
                : '_')
            .ToArray();
        var result = new string(chars).Trim('_');
        return string.IsNullOrEmpty(result) ? fallback : result;
    }

    // IAsyncDisposable

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        await Task.WhenAll(_connections.Values.Select(c => c.Client.DisposeAsync().AsTask()));
        _connections.Clear();
    }
}

// Supporting types

internal sealed record McpServerConnection(
    string Name,
    McpServerDefinition Definition,
    McpClient Client,
    bool IsConnected = false,
    IReadOnlyList<AIFunction>? AgentTools = null)
{
    public IReadOnlyList<AIFunction> AgentTools { get; init; } = AgentTools ?? [];
}

/// <summary>
/// Wraps an <see cref="AIFunction"/> to override its <see cref="Name"/> with a server-prefixed name.
/// Used for MAF <c>TaskAwareMcpClientAIFunction</c> instances that don't expose <c>WithName</c>.
/// </summary>
internal sealed class RenamedAIFunction : AIFunction
{
    private readonly AIFunction _inner;
    private readonly string _name;

    public RenamedAIFunction(AIFunction inner, string name)
    {
        _inner = inner;
        _name = name;
    }

    public override string Name => _name;
    public override string Description => _inner.Description;
    public override JsonElement JsonSchema => _inner.JsonSchema;
    public override JsonElement? ReturnJsonSchema => _inner.ReturnJsonSchema;
    public override JsonSerializerOptions JsonSerializerOptions => _inner.JsonSerializerOptions;

    protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
        => _inner.InvokeAsync(arguments, cancellationToken);
}
