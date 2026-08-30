namespace OneCode.Core.Mcp;

/// <summary>
/// MCP client abstraction over the official ModelContextProtocol SDK.
/// Exposes transport-agnostic operations (connect / list tools / call tool / resources /
/// prompts) so that App-layer consumers do not depend on SDK types.
/// </summary>
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

    Task ConnectStreamableHttpAsync(
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

    Task<IReadOnlyList<McpPrompt>> ListPromptsAsync(CancellationToken ct = default);

    Task<string> GetPromptAsync(
        string name,
        IReadOnlyDictionary<string, string>? arguments = null,
        CancellationToken ct = default);
}
