using Microsoft.Extensions.DependencyInjection;
using OneCode.Core.Mcp;
using OneCode.Infrastructure.Mcp;

namespace OneCode.App.Services.Mcp;

/// <summary>
/// MCP 领域 DI 注册——与 MCP 实现（<see cref="McpConnectionManager"/> / <see cref="McpConfigService"/>）
/// 同目录维护；Infrastructure 侧 MCP 客户端在此一并注册。由组合根 <see cref="OneCode.App.OneCodeApp"/> 显式调用。
/// </summary>
public static class McpServiceCollectionExtensions
{
    public static IServiceCollection AddMcpServices(this IServiceCollection services)
    {
        services.AddSingleton<McpElicitationHandler>(sp =>
            ConsoleMcpElicitationHandler.Create(
                sp.GetRequiredService<ILogger<McpElicitationHandler>>()));
        // Official MCP registry client (used by /mcp search and /mcp install).
        services.AddSingleton<OfficialMcpRegistryClient>();
        services.AddSingleton<McpMultiScopeConfigLoader>();
        services.AddSingleton<McpConfigService>();

        // InProcess MCP 服务器扩展点：宿主/测试注册 IInProcessMcpServerProvider 即可按名接入。
        services.AddSingleton<InProcessMcpServerRegistry>();
        services.AddSingleton<McpConnectionManager>();
        services.AddSingleton<IMcpConnectionManager>(sp => sp.GetRequiredService<McpConnectionManager>());

        services.AddSingleton<McpStartupPreconnector>();

        return services;
    }
}
