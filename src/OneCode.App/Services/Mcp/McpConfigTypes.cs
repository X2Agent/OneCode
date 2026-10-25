namespace OneCode.App.Services.Mcp;

/// <summary>进入配置页时单个 MCP 服务器的快照（配置 + 运行时方法列表）。</summary>
public sealed record McpConfigServerEntry(
    string Name,
    bool Disabled,
    bool Connected,
    IReadOnlyList<string>? EnabledTools,
    IReadOnlyList<string> LiveTools);

/// <summary>配置页保存结果：每个被修改服务器的最终勾选状态。</summary>
public sealed record McpConfigResult(IReadOnlyList<McpConfigServerChange> Servers);

/// <summary>单个服务器的保存结果。EnabledTools 为 null 表示保持「全部暴露」。</summary>
public sealed record McpConfigServerChange(
    string Name,
    bool? Disabled,
    IReadOnlyList<string>? EnabledTools);
