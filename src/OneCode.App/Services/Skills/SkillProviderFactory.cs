using Microsoft.Agents.AI;
using OneCode.App.Skills;
using OneCode.Core.Mcp;
using OneCode.Core.Skills;
using OneCode.Infrastructure.Mcp;

namespace OneCode.App.Services.Skills;

/// <summary>
/// Builds the <see cref="AgentSkillsProvider"/> for one agent run, covering file-based skills
/// (managed/user/project directories + bundled inline skills) and skills served by currently
/// connected MCP servers.
/// </summary>
/// <remarks>
/// <para>
/// <b>Built per run, not cached as a provider.</b> The set of MCP-backed skills is not known when the
/// agent pipeline is first created: MCP servers connect asynchronously in the background and may
/// connect or disconnect at any point. Building the provider per run makes those changes visible on
/// the next run without any provider-swapping machinery.
/// </para>
/// <para>
/// <b>Within a run the work is cached.</b> MAF's builder enables provider-level caching by default,
/// so skills are discovered once and reused for every turn of that run.
/// </para>
/// <para>
/// <b>No skill-index pre-check.</b> An MCP server is passed to <c>UseMcpSkills</c> whenever it is
/// connected. MAF's MCP skills source returns an empty list when <c>skill://index.json</c> is absent,
/// unreadable or empty — the common case for tool-only servers — so filtering first would add an
/// async probe without changing the outcome.
/// </para>
/// </remarks>
public sealed class SkillProviderFactory(
    SkillCatalog catalog,
    IMcpConnectionManager mcpManager,
    ILoggerFactory loggerFactory)
{
    /// <summary>Builds a provider over file, bundled and MCP-served skills.</summary>
    public AgentSkillsProvider Create()
    {
        var builder = new AgentSkillsProviderBuilder();

        var skillDirs = catalog.GetSkillDirectories();
        if (skillDirs.Count > 0)
        {
            builder.UseFileSkills(skillDirs);
            builder.UseFileScriptRunner(SubprocessScriptRunner.CreateRunner(
                loggerFactory.CreateLogger(typeof(SkillProviderFactory))));
        }

        foreach (var bundled in BundledSkills.All.Values)
        {
            builder.UseSkill(new AgentInlineSkill(
                new AgentSkillFrontmatter(bundled.Name, bundled.Description),
                bundled.Prompt));
        }

        foreach (var (_, client) in mcpManager.GetConnectedClients())
        {
            if (client is not McpClient concreteClient || concreteClient.SdkClient is not { } sdkClient)
                continue;

            builder.UseMcpSkills(sdkClient);
        }

        builder.UseLoggerFactory(loggerFactory);
        return builder.Build();
    }
}
