using OneCode.Core.Mcp;
using OneCode.App.Services.Skills;

namespace OneCode.App.Services;

public sealed record PromptRuntimeDependencies(
    IMcpConnectionManager McpConnectionManager,
    McpSkillsIntegrator McpSkillsIntegrator,
    SkillProviderHolder SkillProviderHolder,
    SkillCatalog SkillCatalog);
