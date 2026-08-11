using OneCode.App.Services.Skills;
using OneCode.Infrastructure.Mcp;

namespace OneCode.App.Services;

public sealed record PromptRuntimeDependencies(
    IMcpConnectionManager McpConnectionManager,
    McpSkillsIntegrator McpSkillsIntegrator,
    SkillProviderHolder SkillProviderHolder,
    SkillCatalog SkillCatalog);
