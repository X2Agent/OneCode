using OneCode.App.Services.Skills;
using OneCode.Infrastructure.Skills;

namespace OneCode.Tests;

[Collection(nameof(CurrentDirectoryCollection))]
public sealed class SkillCatalogTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "onecode-skills-" + Guid.NewGuid().ToString("N"));

    public SkillCatalogTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void Frontmatter_ControlsNameVisibilityHintAndBody()
    {
        var content = """
            ---
            name: diagnose
            description: Diagnose an issue
            argument-hint: <issue>
            argument-names: [issue]
            user-invocable: true
            disable-model-invocation: true
            ---
            Investigate {issue}. Frontmatter must not leak.
            """;

        SkillFrontmatterParser.TryParse(content, "fallback", out var skill).Should().BeTrue();
        skill.Name.Should().Be("diagnose");
        skill.ArgumentHint.Should().Be("<issue>");
        skill.ArgumentNames.Should().Equal("issue");
        skill.DisableModelInvocation.Should().BeTrue();
        skill.Body.Should().NotContain("frontmatter:").And.NotContain("name: diagnose");
        SkillCatalog.Render(skill, ["null reference"]).Should().Contain("Investigate null reference.");
    }

    [Fact]
    public void MalformedFrontmatter_IsRejectedInsteadOfLeakingYaml()
    {
        const string content = "---\nname: [broken\n---\nbody";
        SkillFrontmatterParser.TryParse(content, "fallback", out _).Should().BeFalse();
    }

    [Fact]
    public void Render_SupportsArgumentsAndNamedPlaceholders()
    {
        var skill = new SkillDocument("loop", "", "Task={task}; Expected={expected}; All=$ARGUMENTS",
            ArgumentNames: ["task", "expected"]);

        SkillCatalog.Render(skill, ["build", "green"])
            .Should().Be("Task=build; Expected=green; All=build green");
    }

    [Fact]
    public void Catalog_UsesConfiguredWorkingDirectoryAndNameOverride()
    {
        var skillDir = Path.Combine(_root, ".onecode", "skills", "folder-name");
        Directory.CreateDirectory(skillDir);
        File.WriteAllText(Path.Combine(skillDir, "SKILL.md"), """
            ---
            name: overridden
            description: project skill
            user-invocable: true
            ---
            Hello {issue}
            """);

        var catalog = new SkillCatalog(_root);
        var skill = catalog.Find("overridden");

        skill.Should().NotBeNull();
        catalog.GetSkillDirectories().Should().Contain(Path.Combine(_root, ".onecode", "skills"));
        SkillCatalog.Render(skill!, ["world"]).Should().Be("Hello world");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }
}
