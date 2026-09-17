using OneCode.Infrastructure.Git;

namespace OneCode.Tests;

/// <summary>
/// Goal worktree slug 生成回归守护：目录名/分支名必须来自 Goal 描述的可读 token，
/// 且在任何平台（含 Windows 路径与 git ref 规则）下都是合法安全的字符串。
/// 回归场景：早期实现直接使用 32 位 GUID 作目录名，用户无法从 <c>{repo}.worktree/</c>
/// 下辨认哪个目录对应哪个 Goal。
/// </summary>
public sealed class WorktreeLayoutTests
{
    [Fact]
    public void BuildGoalSlug_EnglishSentence_JoinsTokensWithDash()
    {
        var slug = WorktreeLayout.BuildGoalSlug("Refactor StartupFlowCoordinator extract Tui host");

        slug.Should().Be("refactor-startupflowcoordinator-extract-tui-host");
    }

    [Fact]
    public void BuildGoalSlug_ChineseOnly_ReturnsNullForCallerFallback()
    {
        // 中文无法安全用于 git 分支/跨平台路径 → 返回 null，由调用方回退短 id。
        WorktreeLayout.BuildGoalSlug("帮我重构项目启动流程并优化性能").Should().BeNull();
        WorktreeLayout.BuildGoalSlug("   。，；  ").Should().BeNull();
        WorktreeLayout.BuildGoalSlug("").Should().BeNull();
    }

    [Fact]
    public void BuildGoalSlug_MixedCjkAndAscii_KeepsOnlyAsciiTokens()
    {
        var slug = WorktreeLayout.BuildGoalSlug("修复 Bug：empty choices 崩溃");

        slug.Should().Be("bug-empty-choices");
    }

    [Fact]
    public void BuildGoalSlug_LongGoal_TruncatesWithoutTrailingSeparator()
    {
        var goal = string.Join(" ", Enumerable.Repeat("refactor", 20));

        var slug = WorktreeLayout.BuildGoalSlug(goal);

        slug!.Length.Should().BeLessThanOrEqualTo(WorktreeLayout.MaxGoalSlugLength);
        slug.Should().NotEndWith("-");
    }

    [Theory]
    [InlineData("已完成的 Goal")]
    [InlineData("fix: crash / on \"quote\" and 'tick'")]
    [InlineData("路径 C:\\temp\\file.txt")]
    public void BuildGoalSlug_NeverProducesPathOrRefUnsafeCharacters(string goal)
    {
        var slug = WorktreeLayout.BuildGoalSlug(goal);

        if (slug is null)
            return;
        slug.Should().MatchRegex("^[a-z0-9-]+$");
        slug.Should().NotContain("..");
    }
}
