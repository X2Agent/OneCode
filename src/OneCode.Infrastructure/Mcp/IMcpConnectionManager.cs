using Microsoft.Extensions.AI;

namespace OneCode.Infrastructure.Mcp;

/// <summary>
/// MCP connection manager abstraction.
/// Implemented by OneCode.App.Services.Mcp.McpConnectionManager.
/// This is the single source of truth for runtime connection state — both the
/// LLM tool catalog and the /mcp commands read from the same connection pool.
/// </summary>
public interface IMcpConnectionManager : IAsyncDisposable
{
    event Action? ServersChanged;

    /// <summary>Connect to every enabled server found in the multi-scope config files (startup use).</summary>
    Task ConnectAllAsync(CancellationToken ct = default);

    /// <summary>
    /// Hot-connect a single server by name, loading its definition from the config files.
    /// Used by <c>/mcp connect</c> and after <c>/mcp install</c>/<c>/mcp add</c>.
    /// Returns false if the server is not configured or connection failed.
    /// </summary>
    Task<bool> ConnectOneAsync(string name, CancellationToken ct = default);

    /// <summary>Disconnect a single server (runtime state only; config is preserved).</summary>
    Task DisconnectAsync(string name);

    /// <summary>Reconnect a server (disconnect + re-connect from config).</summary>
    Task ReconnectServerAsync(string name, CancellationToken ct = default);

    IReadOnlyList<string> GetServerNames();

    /// <summary>Lookup a connected client by server name (null if not connected).</summary>
    McpClient? GetClient(string name);

    IReadOnlyList<(string Name, McpClient Client)> GetConnectedClients();

    IReadOnlyList<(string Name, McpServerDefinition Definition)> GetServerDefinitions();

    /// <summary>Runtime status snapshot: server name, connection state, tool count.</summary>
    IReadOnlyList<McpServerStatus> GetStatus();

    /// <summary>All live MCP tools from connected servers (merged into the LLM tool catalog).</summary>
    IReadOnlyList<AIFunction> GetAllTools();
}
