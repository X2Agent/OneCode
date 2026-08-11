using Microsoft.Agents.AI;
using OneCode.Infrastructure.Mcp;

namespace OneCode.App.Services.Skills;

/// <summary>
/// MCP 技能集成：对支持 <c>skill://index.json</c> 的 server 注册 MAF <c>UseMcpSkills</c>。
/// </summary>
public sealed class McpSkillsIntegrator
{
    private const string SkillIndexUri = "skill://index.json";

    private readonly IMcpConnectionManager _mcpManager;
    private readonly ILogger<McpSkillsIntegrator> _logger;

    public McpSkillsIntegrator(
        IMcpConnectionManager mcpManager,
        ILogger<McpSkillsIntegrator> logger)
    {
        _mcpManager = mcpManager;
        _logger = logger;
    }

    /// <summary>
    /// 向 builder 注册支持 skill:// 协议的 MCP server 技能源。
    /// </summary>
    public async Task ApplyAsync(AgentSkillsProviderBuilder builder, CancellationToken ct = default)
    {
        foreach (var (serverName, client) in _mcpManager.GetConnectedClients())
        {
            var sdkClient = client.SdkClient;
            if (sdkClient is null)
                continue;

            try
            {
                if (!await HasSkillIndexAsync(client, ct).ConfigureAwait(false))
                    continue;

                builder.UseMcpSkills(sdkClient);
                _logger.LogInformation(
                    "MCP server '{Server}': registered MAF skills via {IndexUri}",
                    serverName, SkillIndexUri);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(
                    ex,
                    "MCP server '{Server}': skill:// discovery failed, skipping",
                    serverName);
            }
        }
    }

    private async Task<bool> HasSkillIndexAsync(McpClient client, CancellationToken ct)
    {
        try
        {
            var content = await client.ReadResourceAsync(SkillIndexUri, ct).ConfigureAwait(false);
            return !string.IsNullOrWhiteSpace(content);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "MCP skill:// discovery failed for a server, skipping");
            return false;
        }
    }
}
