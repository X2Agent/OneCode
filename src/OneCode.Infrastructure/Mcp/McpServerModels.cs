namespace OneCode.Infrastructure.Mcp;

/// <summary>
/// Runtime status snapshot of one MCP server connection (name, definition, connection
/// state, tool count). Used by <c>/mcp list</c> and <c>/doctor</c> to show runtime state.
/// </summary>
public sealed record McpServerStatus(
    string Name,
    McpServerDefinition Definition,
    bool IsConnected,
    int ToolCount);
