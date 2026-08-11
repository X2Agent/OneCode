using System.ComponentModel;
using OneCode.Core.Errors;
using OneCode.Infrastructure.Mcp;

namespace OneCode.App.Tools;

/// <summary>
/// 列出 MCP 资源 — 列出连接的 MCP 服务器上可用的资源。
/// </summary>
public sealed class ListMcpResourcesTool
{
    private readonly IMcpConnectionManager _connectionManager;

    public ListMcpResourcesTool(IMcpConnectionManager connectionManager) => _connectionManager = connectionManager;

    [Description("List available MCP resources from connected servers.")]
    public async Task<ToolResult> ListResourcesAsync(
        [Description("MCP server name (optional, lists all if omitted)")] string? server = null,
        CancellationToken ct = default)
    {
        List<object> resources = [];

        if (!string.IsNullOrEmpty(server))
        {
            var client = _connectionManager.GetClient(server);
            if (client != null)
            {
                var list = await client.ListResourcesAsync(ct).ConfigureAwait(false);
                resources.AddRange(list.Select(r => new { server, uri = r.Uri, name = r.Name, description = r.Description }));
            }
        }
        else
        {
            List<string> errors = [];
            foreach (var (name, client) in _connectionManager.GetConnectedClients())
            {
                try
                {
                    var list = await client.ListResourcesAsync(ct).ConfigureAwait(false);
                    resources.AddRange(list.Select(r => new { server = name, uri = r.Uri, name = r.Name, description = r.Description }));
                }
                catch (Exception ex)
                {
                    errors.Add($"{name}: {ex.Message}");
                }
            }

            if (errors.Count > 0 && resources.Count == 0)
                return ToolResult.Error(AgentProblemDetails.ServiceUnavailable($"All MCP servers failed: {string.Join("; ", errors)}", toolName: "McpTool"));

            if (errors.Count > 0)
                resources.Add(new { server = "_errors", uri = "", name = "partial_failure", description = string.Join("; ", errors) });
        }

        return ToolResult.JsonSuccess(new { resources });
    }
}

/// <summary>
/// 读取 MCP 资源 — 读取 MCP 服务器上指定 URI 的资源内容。
/// </summary>
public sealed class ReadMcpResourceTool
{
    private readonly IMcpConnectionManager _connectionManager;

    public ReadMcpResourceTool(IMcpConnectionManager connectionManager) => _connectionManager = connectionManager;

    [Description("Read a resource from an MCP server by URI.")]
    public async Task<ToolResult> ReadResourceAsync(
        [Description("MCP server name")] string server,
        [Description("Resource URI")] string uri,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(server) || string.IsNullOrEmpty(uri))
            return ToolResult.Error(AgentProblemDetails.ToolExecutionFailed("server and uri are required", toolName: "McpTool"));

        var client = _connectionManager.GetClient(server);
        if (client == null)
            return ToolResult.Error(AgentProblemDetails.ServiceUnavailable($"MCP server '{server}' not connected", toolName: "McpTool"));

        try
        {
            var content = await client.ReadResourceAsync(uri, ct).ConfigureAwait(false);
            return ToolResult.JsonSuccess(new { uri, content });
        }
        catch (Exception ex)
        {
            return ToolResult.Error(AgentProblemDetails.ToolExecutionFailed(ex.Message, toolName: "ReadMcpResource", suggestedNextAction: "检查 MCP 服务器状态"));
        }
    }
}
