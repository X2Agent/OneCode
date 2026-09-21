using OneCode.App.Services.Skills;

namespace OneCode.Tests;

/// <summary>
/// §4.3.1 行为契约：模型入口与斜杠入口必须用同一条发现规则。
/// </summary>
/// <remarks>
/// 修复前两条入口各自扫描：模型侧走 MAF 的递归 <c>SKILL.md</c>，斜杠侧走产品自己的
/// 「顶层 <c>*.md</c> + 直接子目录 <c>SKILL.md</c>」并用产品 parser。
/// 同一仓库因此可能对一侧可见、对另一侧不可见，或同名给出不同正文。
/// </remarks>
public sealed class SkillDiscoveryRuleTests : IDisposable
{
    private readonly string _root;

    public SkillDiscoveryRuleTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"SkillDiscovery_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void IsSharedSkillFile_MatchesSkillMdCaseInsensitively()
    {
        SkillDiscovery.IsSharedSkillFile(Path.Combine(_root, "SKILL.md")).Should().BeTrue();
        SkillDiscovery.IsSharedSkillFile(Path.Combine(_root, "skill.md")).Should().BeTrue();
        SkillDiscovery.IsSharedSkillFile(Path.Combine(_root, "notes.md")).Should().BeFalse();
    }

    /// <summary>
    /// 反证：嵌套的 SKILL.md 必须被发现——模型侧是递归的，斜杠侧若只看直接子目录就会漏掉它们。
    /// </summary>
    [Fact]
    public void EnumerateSharedSkillDirectories_FindsNestedSkills()
    {
        WriteSharedSkill("outer", "outer skill");
        WriteSharedSkill(Path.Combine("group", "inner"), "inner skill");

        var found = SkillDiscovery
            .EnumerateSharedSkillDirectories(_root, maxDepth: 2)
            .Select(dir => Path.GetFileName(dir))
            .ToList();

        found.Should().Contain("outer");
        found.Should().Contain("inner", "the model-side rule is recursive, so the slash side must be too");
    }

    /// <summary>深度上限必须与 MAF 的默认一致，否则两侧仍会看到不同的技能集。</summary>
    [Fact]
    public void EnumerateSharedSkillDirectories_RespectsDepthLimit()
    {
        WriteSharedSkill(Path.Combine("a", "b", "c", "too-deep"), "unreachable");

        var found = SkillDiscovery
            .EnumerateSharedSkillDirectories(_root, maxDepth: 2)
            .Select(dir => Path.GetFileName(dir))
            .ToList();

        found.Should().NotContain("too-deep");
    }

    /// <summary>
    /// SKILL.md 所在目录即技能根：MAF 不会继续向下找，枚举也必须停在那里。
    /// </summary>
    [Fact]
    public void EnumerateSharedSkillDirectories_DoesNotDescendPastSkillRoot()
    {
        WriteSharedSkill("parent", "parent skill");
        WriteSharedSkill(Path.Combine("parent", "child"), "should not be treated as its own skill");

        var found = SkillDiscovery
            .EnumerateSharedSkillDirectories(_root, maxDepth: 3)
            .Select(dir => Path.GetFileName(dir))
            .ToList();

        found.Should().Contain("parent");
        found.Should().NotContain("child");
    }

    /// <summary>
    /// 反证：顶层 *.md 是**仅供用户调用**的布局，不得进入共享集合——MAF 不读它，
    /// 提升它等于悄悄扩大模型可调用的技能范围。
    /// </summary>
    [Fact]
    public void LoadUserOnlySkills_ExcludesSkillMdAndKeepsLegacyFiles()
    {
        WriteFile("legacy-command.md", """
            ---
            name: legacy-command
            description: a user-only slash command
            ---
            Do the thing with $ARGUMENTS
            """);
        WriteFile("SKILL.md", """
            ---
            name: shared
            description: shared skill
            ---
            shared body
            """);

        var userOnly = SkillDiscovery.LoadUserOnlySkills(_root).ToList();

        userOnly.Should().ContainSingle();
        userOnly[0].Name.Should().Be("legacy-command");
        userOnly.Should().NotContain(d => d.Name == "shared",
            "SKILL.md belongs to the shared set, which both entry points read");
    }

    /// <summary>用户可调用性由 frontmatter 决定，显式关闭的条目不得出现在斜杠集合里。</summary>
    [Fact]
    public void LoadUserOnlySkills_HonoursUserInvocableFalse()
    {
        WriteFile("hidden.md", """
            ---
            name: hidden
            description: model-only helper
            user-invocable: false
            ---
            body
            """);

        SkillDiscovery.LoadUserOnlySkills(_root).Should().BeEmpty();
    }

    private void WriteSharedSkill(string relativeDirectory, string body) =>
        WriteFile(Path.Combine(relativeDirectory, "SKILL.md"), $"""
            ---
            name: {Path.GetFileName(relativeDirectory)}
            description: {body}
            ---
            {body}
            """);

    private void WriteFile(string relativePath, string content)
    {
        var path = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }
}
