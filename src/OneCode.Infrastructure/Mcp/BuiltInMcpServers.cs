using OneCode.Core.Mcp;
namespace OneCode.Infrastructure.Mcp;

/// <summary>
/// 内置 MCP 服务定义。这些服务随 OneCode 预置，用户无需手动配置即可使用，
/// 也可在自己的 .mcp.json 中同名覆盖（改 command/args 等）或通过 disabled 禁用。
///
/// <para>连接策略约定：内置服务默认 <b>按需连接</b>（不随启动连接），由消费方
/// （如 /design-init 的 website clone、模型直接调用 playwright 工具）在首次使用时
/// 通过 <see cref="IMcpConnectionManager.ConnectOneAsync"/> 触发。
/// 这是"正向约定"——内置服务是可选增强能力，不应拖慢或污染启动，也无需
/// 用户在配置里表达连接时机（不引入 lazy/startup 标记）。</para>
///
/// <para>playwright 默认 <b>无头模式</b>（--headless）：浏览器渲染由 <c>BrowserFetch</c>
/// 工具按需触发（SSRF 校验 → ConnectOneAsync → navigate+snapshot 一次调用完成），
/// WebFetch 的 JS-only 降级 hint 引导模型决定是否调用。不应在用户桌面弹出可见
/// 浏览器窗口；agent 交互走 ARIA snapshot 同样不受影响。
/// 需要可见浏览器的用户可在 .mcp.json 中同名覆盖去掉该参数。</para>
/// </summary>
public static class BuiltInMcpServers
{
    private static readonly IReadOnlyDictionary<string, McpServerDefinition> Servers =
        new Dictionary<string, McpServerDefinition>(StringComparer.OrdinalIgnoreCase)
        {
            ["playwright"] = new(
                McpTransportType.Stdio,
                Command: "npx",
                Args: ["-y", "@playwright/mcp@latest", "--headless"],
                InitTimeoutMs: 120_000),
        };

    public static IReadOnlyDictionary<string, McpServerDefinition> All => Servers;

    /// <summary>给定服务器名是否为内置服务（按名称判断，忽略大小写）。</summary>
    public static bool IsBuiltIn(string name) => Servers.ContainsKey(name);
}
