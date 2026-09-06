using Microsoft.Extensions.AI;

namespace OneCode.Core.Mcp;

/// <summary>
/// MCP connection manager abstraction.
/// Implemented by OneCode.App.Services.Mcp.McpConnectionManager.
/// This is the single source of truth for runtime connection state — both the
/// LLM tool catalog and the /mcp commands read from the same connection pool.
/// </summary>
/// <remarks>
/// 成员数超过 src/AGENTS.md §11.2 的"接口 ≤5 成员"原则：本接口是连接池运行时状态的
/// 单一真相源（工具目录、/mcp 命令、状态栏、TUI 配置页共同消费），拆分为多个视图接口
/// 会迫使各消费方注入多个实现引用并割裂事件订阅语义，属规范允许的评审豁免。
/// </remarks>
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

    /// <summary>
    /// Names of servers with an entry in the connection pool — including ones whose
    /// connection attempt failed (soft-failure). A missing name is therefore not proof
    /// that it is unconfigured; use <see cref="GetStatus"/> (or the config loader) to
    /// distinguish "not configured" from "configured but disconnected".
    /// </summary>
    IReadOnlyList<string> GetServerNames();

    /// <summary>Lookup a connected client by server name (null if not connected).</summary>
    IMcpClient? GetClient(string name);

    IReadOnlyList<(string Name, IMcpClient Client)> GetConnectedClients();

    /// <summary>Runtime status snapshot: server name, connection state, tool count, last error.</summary>
    IReadOnlyList<McpServerStatus> GetStatus();

    /// <summary>
    /// 连接池三态快照（连接中 x/y / 已连 n / 失败 k）——状态栏与欢迎页 MCP 诊断的数据源。
    /// </summary>
    McpConnectionSummary GetConnectionSummary();

    /// <summary>All live MCP tools from connected servers (merged into the LLM tool catalog).</summary>
    IReadOnlyList<AIFunction> GetAllTools();

    /// <summary>
    /// Re-apply the per-server tool whitelist to an already-connected server without
    /// dropping the connection. Used after <c>/mcp enable-tool/disable-tool</c> and the
    /// TUI config overlay to hot-apply selection changes. Returns false if the server
    /// is not currently connected.
    /// </summary>
    Task<bool> ReloadToolsAsync(string name, CancellationToken ct = default);
}
