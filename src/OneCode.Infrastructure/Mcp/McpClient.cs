using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using OneCode.Core.Product;

namespace OneCode.Infrastructure.Mcp;

/// <summary>
/// MCP client wrapper using the official ModelContextProtocol SDK.
/// Supports stdio, SSE, and HTTP (Streamable) transports.
/// </summary>
public sealed class McpClient : IAsyncDisposable
{
    private readonly ILogger<McpClient> _logger;
    private readonly McpElicitationHandler? _elicitationHandler;
    private global::ModelContextProtocol.Client.McpClient? _client;
    private IClientTransport? _transport;
    private bool _disposed;

    /// <summary>底层 MCP SDK 客户端，供 MAF <c>ListAgentToolsWithTaskSupportAsync</c> 等扩展使用。</summary>
    public global::ModelContextProtocol.Client.McpClient? SdkClient => _client;

    public McpClient(ILogger<McpClient> logger, McpElicitationHandler? elicitationHandler = null)
    {
        _logger = logger;
        _elicitationHandler = elicitationHandler;
    }

    // stdio transport

    /// <summary>
    /// Connect to an MCP server via stdio transport.
    /// </summary>
    public async Task ConnectStdioAsync(
        string command,
        IReadOnlyList<string> args,
        IReadOnlyDictionary<string, string>? env = null,
        CancellationToken ct = default)
    {
        _logger.LogInformation("Connecting to MCP server (stdio): {Command} {Args}", command, string.Join(" ", args));

        _transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Command = command,
            Arguments = args.ToArray(),
            EnvironmentVariables = env?.ToDictionary(kv => kv.Key, kv => (string?)kv.Value),
            Name = $"stdio:{command}",
        });

        _client = await CreateClientAsync(ct).ConfigureAwait(false);
        LogConnectionInfo();
    }

    // SSE transport

    /// <summary>
    /// Connect to an MCP server via SSE transport.
    /// </summary>
    public async Task ConnectSseAsync(
        string url,
        IReadOnlyDictionary<string, string>? headers = null,
        CancellationToken ct = default)
    {
        _logger.LogInformation("Connecting to MCP server (SSE): {Url}", url);

        _transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(url, UriKind.RelativeOrAbsolute),
            TransportMode = HttpTransportMode.Sse,
            AdditionalHeaders = headers?.ToDictionary(kv => kv.Key, kv => kv.Value),
            Name = $"sse:{url}",
        });

        _client = await CreateClientAsync(ct).ConfigureAwait(false);
        LogConnectionInfo();
    }

    // HTTP / Streamable transport

    /// <summary>
    /// Connect to an MCP server via HTTP (Streamable) transport.
    /// Uses auto-detect to choose between Streamable HTTP and SSE.
    /// </summary>
    public async Task ConnectHttpAsync(
        string url,
        IReadOnlyDictionary<string, string>? headers = null,
        CancellationToken ct = default)
    {
        _logger.LogInformation("Connecting to MCP server (HTTP): {Url}", url);

        _transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(url, UriKind.RelativeOrAbsolute),
            TransportMode = HttpTransportMode.AutoDetect,
            AdditionalHeaders = headers?.ToDictionary(kv => kv.Key, kv => kv.Value),
            Name = $"http:{url}",
        });

        _client = await CreateClientAsync(ct).ConfigureAwait(false);
        LogConnectionInfo();
    }

    /// <summary>
    /// Connect to an MCP server via Streamable HTTP transport (explicit, no auto-detect).
    /// </summary>
    public async Task ConnectStreamableHttpAsync(
        string url,
        IReadOnlyDictionary<string, string>? headers = null,
        CancellationToken ct = default)
    {
        _logger.LogInformation("Connecting to MCP server (Streamable HTTP): {Url}", url);

        _transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(url, UriKind.RelativeOrAbsolute),
            TransportMode = HttpTransportMode.StreamableHttp,
            AdditionalHeaders = headers?.ToDictionary(kv => kv.Key, kv => kv.Value),
            Name = $"streamable-http:{url}",
        });

        _client = await CreateClientAsync(ct).ConfigureAwait(false);
        LogConnectionInfo();
    }

    // Generic transport-based connection

    /// <summary>
    /// Connect using a custom transport instance.
    /// </summary>
    public async Task ConnectAsync(IClientTransport transport, CancellationToken ct = default)
    {
        _transport = transport;
        _logger.LogInformation("Connecting to MCP server via transport: {Name}", transport.Name);

        _client = await CreateClientAsync(ct).ConfigureAwait(false);
        LogConnectionInfo();
    }

    // Core operations

    /// <summary>
    /// List available tools from the connected server.
    /// </summary>
    public async Task<IReadOnlyList<McpTool>> ListToolsAsync(CancellationToken ct = default)
    {
        if (_client == null)
            throw new InvalidOperationException("Not connected to an MCP server");

        var tools = await _client.ListToolsAsync(cancellationToken: ct).ConfigureAwait(false);
        return tools.Select(t =>
        {
            JsonElement? schema = null;
            try
            {
                if (t.ProtocolTool?.InputSchema is { } protocolSchema)
                {
                    schema = JsonSerializer.SerializeToElement(protocolSchema);
                }
                else if (t is Microsoft.Extensions.AI.AIFunction aiFunc && aiFunc.JsonSchema is { } aiSchema)
                {
                    schema = aiSchema;
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to extract input schema for tool {Name}", t.Name);
            }

            return new McpTool(t.Name, t.Description, schema);
        }).ToList();
    }

    /// <summary>
    /// Call a tool on the connected server.
    /// </summary>
    public async Task<McpToolResult> CallToolAsync(
        string name,
        Dictionary<string, object?>? arguments = null,
        CancellationToken ct = default)
    {
        if (_client == null)
            throw new InvalidOperationException("Not connected to an MCP server");

        _logger.LogDebug("Calling MCP tool: {Name}", name);
        var response = await _client.CallToolAsync(name, arguments, cancellationToken: ct).ConfigureAwait(false);

        var content = string.Join("\n", response.Content.Select(c => c switch
        {
            ModelContextProtocol.Protocol.TextContentBlock text => text.Text,
            _ => c.ToString() ?? ""
        }));
        return new McpToolResult(content, response.IsError ?? false);
    }

    /// <summary>
    /// List available resources from the connected server.
    /// </summary>
    public async Task<IReadOnlyList<McpResource>> ListResourcesAsync(CancellationToken ct = default)
    {
        if (_client == null)
            throw new InvalidOperationException("Not connected to an MCP server");

        var resources = await _client.ListResourcesAsync(cancellationToken: ct).ConfigureAwait(false);
        return resources.Select(r => new McpResource(r.Uri?.ToString() ?? "", r.Name ?? "", r.Description ?? "")).ToList();
    }

    /// <summary>
    /// Read a resource from the connected server.
    /// </summary>
    public async Task<string> ReadResourceAsync(string uri, CancellationToken ct = default)
    {
        if (_client == null)
            throw new InvalidOperationException("Not connected to an MCP server");

        var result = await _client.ReadResourceAsync(uri, cancellationToken: ct).ConfigureAwait(false);
        return string.Join("\n", result.Contents.Select(c => c switch
        {
            ModelContextProtocol.Protocol.TextResourceContents textContent => textContent.Text ?? "",
            ModelContextProtocol.Protocol.BlobResourceContents blobContent => Convert.ToBase64String(blobContent.Blob.ToArray()),
            _ => c.ToString() ?? ""
        }));
    }

    /// <summary>
    /// List available prompts from the connected server.
    /// </summary>
    public async Task<IReadOnlyList<McpPrompt>> ListPromptsAsync(CancellationToken ct = default)
    {
        if (_client == null)
            throw new InvalidOperationException("Not connected to an MCP server");

        var prompts = await _client.ListPromptsAsync(cancellationToken: ct).ConfigureAwait(false);
        return prompts.Select(p => new McpPrompt(
            p.Name,
            p.Description,
            p.ProtocolPrompt.Arguments?.Select(a => a.Name).ToArray() ?? []
        )).ToList();
    }

    /// <summary>
    /// Get a specific prompt, optionally with arguments, returning the rendered text.
    /// </summary>
    public async Task<string> GetPromptAsync(
        string name,
        IReadOnlyDictionary<string, string>? arguments = null,
        CancellationToken ct = default)
    {
        if (_client == null)
            throw new InvalidOperationException("Not connected to an MCP server");

        var args = arguments?.ToDictionary(kv => kv.Key, kv => (object?)kv.Value);
        var result = await _client.GetPromptAsync(name, args, cancellationToken: ct).ConfigureAwait(false);
        return string.Join("\n", result.Messages.Select(m => m.Content switch
        {
            ModelContextProtocol.Protocol.TextContentBlock text => text.Text ?? "",
            _ => m.Content?.ToString() ?? ""
        }));
    }

    /// <summary>
    /// Check if connected to a server.
    /// </summary>
    public bool IsConnected => _client != null;

    // Implementation details

    private async Task<global::ModelContextProtocol.Client.McpClient> CreateClientAsync(CancellationToken ct)
    {
        var options = new McpClientOptions
        {
            ClientInfo = new Implementation { Name = ProductInfo.Default.Name, Version = ProductInfo.Default.Version },
            Capabilities = new ClientCapabilities
            {
                Elicitation = new ElicitationCapability()
            }
        };

        if (_elicitationHandler != null)
        {
            options.Handlers = new McpClientHandlers
            {
                ElicitationHandler = async (requestParams, token) =>
                {
                    if (requestParams == null)
                        return new ElicitResult { Action = "cancel" };

                    var elicitationRequest = new McpElicitationPrompt(
                        ServerName: _transport?.Name ?? "unknown",
                        Message: requestParams.Message,
                        Schema: requestParams.RequestedSchema != null
                            ? JsonSerializer.Serialize(requestParams.RequestedSchema)
                            : null,
                        Url: requestParams.Url);

                    var response = await _elicitationHandler.HandleElicitationAsync(elicitationRequest, token)
                        .ConfigureAwait(false);

                    var sdkResult = new ElicitResult
                    {
                        Action = response.Action switch
                        {
                            ElicitationAction.Accept => "accept",
                            ElicitationAction.Decline => "decline",
                            _ => "cancel"
                        }
                    };

                    if (response.Data != null)
                    {
                        try
                        {
                            var contentDict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(response.Data);
                            if (contentDict != null)
                                sdkResult.Content = contentDict;
                        }
                        catch
                        {
                            sdkResult.Content = new Dictionary<string, JsonElement>
                            {
                                ["value"] = JsonSerializer.SerializeToElement(response.Data)
                            };
                        }
                    }

                    return sdkResult;
                }
            };
        }

        return await global::ModelContextProtocol.Client.McpClient.CreateAsync(
            _transport!,
            options,
            cancellationToken: ct).ConfigureAwait(false);
    }

    private void LogConnectionInfo()
    {
        if (_client?.ServerInfo != null)
        {
            _logger.LogInformation("Connected to MCP server: {Name} v{Version}",
                _client.ServerInfo.Name, _client.ServerInfo.Version);
        }
    }

    // Disposal

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;

        try
        {
            if (_client != null)
            {
                await _client.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            _client = null;

            try
            {
                if (_transport is IAsyncDisposable asyncDisposable)
                {
                    await asyncDisposable.DisposeAsync().ConfigureAwait(false);
                }
            }
            finally
            {
                _transport = null;
            }
        }
    }
}

// Data types

/// <summary>
/// MCP resource definition.
/// </summary>
public sealed record McpResource(string Uri, string Name, string Description);

/// <summary>
/// MCP tool definition.
/// </summary>
public sealed record McpTool(
    string Name,
    string? Description = null,
    JsonElement? InputSchema = null);

/// <summary>
/// MCP tool execution result.
/// </summary>
public sealed record McpToolResult(
    string Content,
    bool IsError = false);

/// <summary>
/// MCP prompt definition.
/// </summary>
public sealed record McpPrompt(
    string Name,
    string? Description,
    string[] ArgumentNames);
