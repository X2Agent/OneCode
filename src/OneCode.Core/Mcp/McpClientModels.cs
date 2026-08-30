namespace OneCode.Core.Mcp;

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
