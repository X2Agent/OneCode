using Microsoft.Agents.AI;
using OneCode.App.Skills;
using OneCode.Core.Skills;

namespace OneCode.App.Services.Skills;

/// <summary>
/// 构建 AgentSkillsProvider 的公共配置：文件技能目录 + BundledSkills。
/// </summary>
internal static class AgentSkillsProviderFactory
{
    public static void ConfigureFileAndBundledSkills(
        AgentSkillsProviderBuilder builder,
        SkillCatalog catalog,
        ILogger? scriptLogger = null)
    {
        var skillDirs = catalog.GetSkillDirectories();
        if (skillDirs.Count > 0)
        {
            builder.UseFileSkills(skillDirs);
            builder.UseFileScriptRunner(SubprocessScriptRunner.CreateRunner(scriptLogger));
        }

        foreach (var bundled in BundledSkills.All.Values)
        {
            builder.UseSkill(new AgentInlineSkill(
                new AgentSkillFrontmatter(bundled.Name, bundled.Description),
                bundled.Prompt));
        }
    }

}
