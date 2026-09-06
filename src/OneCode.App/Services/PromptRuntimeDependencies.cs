using OneCode.App.Services.Skills;

namespace OneCode.App.Services;

public sealed record PromptRuntimeDependencies(
    McpSkillsIntegrator McpSkillsIntegrator,
    SkillProviderHolder SkillProviderHolder,
    SkillCatalog SkillCatalog);
