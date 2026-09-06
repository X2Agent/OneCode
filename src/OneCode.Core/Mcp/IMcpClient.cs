namespace OneCode.Core.Mcp;

/// <summary>
/// MCP client abstraction over the official ModelContextProtocol SDK.
/// Exposes transport-agnostic operations (connect / list tools / call tool / resources)
/// so that App-layer consumers do not depend on SDK types.
/// </summary>
/// <remarks>
/// 成员数超过 src/AGENTS.md §11.2 的"接口 ≤5 成员"原则：传输建立（stdio/sse/http/自定义
/// transport）与协议操作（tools/resources）构成同一客户端抽象的内聚能力面，按传输或
/// 操作拆分会迫使连接层按传输类型注入不同接口，复杂度不降反升，属规范允许的评审豁免。
/// </remarks>
public interface IMcpClient : IAsyncDisposable
{
    bool IsConnected { get; }

    Task ConnectStdioAsync(
        string command,
        IReadOnlyList<string> args,
        IReadOnlyDictionary<string, string>? env = null,
        CancellationToken ct = default);

    Task ConnectSseAsync(
        string url,
        IReadOnlyDictionary<string, string>? headers = null,
        CancellationToken ct = default);

    Task ConnectHttpAsync(
        string url,
        IReadOnlyDictionary<string, string>? headers = null,
        CancellationToken ct = default);

    Task<IReadOnlyList<McpTool>> ListToolsAsync(CancellationToken ct = default);

    Task<McpToolResult> CallToolAsync(
        string name,
        Dictionary<string, object?>? arguments = null,
        CancellationToken ct = default);

    Task<IReadOnlyList<McpResource>> ListResourcesAsync(CancellationToken ct = default);

    Task<string> ReadResourceAsync(string uri, CancellationToken ct = default);
}
