using OneCode.App.Session;
using OneCode.App.Tui;

namespace OneCode.Tests;

/// <summary>
/// TypeaheadCompletionEngine 的会话 id 补全模式（第 4 模式）：
/// <c>/session switch|delete &lt;前缀&gt;</c> 与 <c>/close &lt;前缀&gt;</c> 触发，
/// popup 显示短 id + 标题，接受后插入完整 id。
/// </summary>
public sealed class TypeaheadCompletionEngineTests
{
    private static readonly string FullIdA = "4f8a90c1e0d244ef8dba35b2588c0424";
    private static readonly string FullIdB = "4f8a90c1ffffffffffffffffffffffff";

    private static TypeaheadCompletionEngine CreateEngine(
        Func<IReadOnlyList<SessionCandidate>>? provider) =>
        new([], Directory.GetCurrentDirectory(), sessionCandidatesProvider: provider);

    private static SessionCandidate Candidate(string fullId, string title = "会话标题", string mode = "plan") =>
        new(fullId, title, mode, 27);

    [Fact]
    public void GetCompletionItems_SwitchPrefix_ShowsTitleInsertsFullId()
    {
        var engine = CreateEngine(() => [Candidate(FullIdA), Candidate(FullIdB)]);

        var items = engine.GetCompletionItems("/session switch 4f8a90c1e0");

        items.Should().HaveCount(1);
        items[0].Display.Should().Contain("4f8a90c1").And.Contain("会话标题");
        items[0].Insert.Should().Be($"/session switch {FullIdA} ");
    }

    [Fact]
    public void GetCompletionItems_EmptyPrefix_ReturnsAllCandidates()
    {
        var engine = CreateEngine(() => [Candidate(FullIdA), Candidate(FullIdB)]);

        var items = engine.GetCompletionItems("/session switch ");

        items.Should().HaveCount(2);
        items.Should().OnlyContain(i => i.Insert.StartsWith("/session switch 4f8a"));
    }

    [Fact]
    public void GetCompletionItems_DeleteSubcommand_Triggers()
    {
        var engine = CreateEngine(() => [Candidate(FullIdA)]);

        var items = engine.GetCompletionItems($"/session delete {FullIdA[..4]}");

        items.Should().HaveCount(1);
        items[0].Insert.Should().StartWith("/session delete 4f8a90c1");
    }

    [Fact]
    public void GetCompletionItems_CloseCommand_Triggers()
    {
        var engine = CreateEngine(() => [Candidate(FullIdA)]);

        var items = engine.GetCompletionItems("/close 4f8a");

        items.Should().HaveCount(1);
        items[0].Insert.Should().Be($"/close {FullIdA} ");
    }

    [Fact]
    public void GetCompletions_ProviderNull_ReturnsEmpty()
    {
        var engine = CreateEngine(null);

        engine.GetCompletions("/session switch 4f").Should().BeEmpty();
    }

    [Fact]
    public void GetCompletions_UnrelatedInput_DoesNotTrigger()
    {
        var engine = CreateEngine(() => [Candidate(FullIdA)]);

        engine.GetCompletions("/hello").Should().BeEmpty();
    }

    [Fact]
    public void GetCompletionItems_BareAt_ListsWorkspaceFilesFlatAndSkipsIgnoredDirs()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"onecode-at-flat-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(tempDir, "src", "Services"));
        Directory.CreateDirectory(Path.Combine(tempDir, "docs", "empty"));
        Directory.CreateDirectory(Path.Combine(tempDir, ".git"));
        Directory.CreateDirectory(Path.Combine(tempDir, "bin", "Debug"));
        Directory.CreateDirectory(Path.Combine(tempDir, "obj"));
        try
        {
            File.WriteAllText(Path.Combine(tempDir, "README.md"), "# readme");
            File.WriteAllText(Path.Combine(tempDir, "src", "Services", "CronJobExecutor.cs"), "// code");
            File.WriteAllText(Path.Combine(tempDir, ".git", "config"), "git");
            File.WriteAllText(Path.Combine(tempDir, "bin", "Debug", "app.dll"), "bin");
            File.WriteAllText(Path.Combine(tempDir, "obj", "project.assets.json"), "{}");

            var engine = new TypeaheadCompletionEngine([], tempDir);
            var items = engine.GetCompletionItems("@");

            var expectedNested = Path.Combine("src", "Services", "CronJobExecutor.cs");
            items.Select(i => i.Display).Should().Equal("README.md", expectedNested);
            items[0].Insert.Should().Be("@README.md ");
            items[1].Insert.Should().Be($"@{expectedNested} ");
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void GetCompletions_AtFileFuzzySearch_RanksFilenamePrefixBeforeSubstringAndSubsequence()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"onecode-fuzzy-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(tempDir, "src", "Tui"));
        Directory.CreateDirectory(Path.Combine(tempDir, "src", "Tests"));
        Directory.CreateDirectory(Path.Combine(tempDir, "src", "Services"));
        try
        {
            File.WriteAllText(Path.Combine(tempDir, "src", "Tui", "ChatInputView.cs"), "// view");
            File.WriteAllText(Path.Combine(tempDir, "src", "Tests", "MyChatInputHelper.cs"), "// helper");
            File.WriteAllText(Path.Combine(tempDir, "src", "Services", "CronJobExecutor.cs"), "// cron");

            var engine = new TypeaheadCompletionEngine([], tempDir);

            var prefixAndSubstring = engine.GetCompletions("check @ChatInput");
            var expectedPrefix = $"check @{Path.Combine("src", "Tui", "ChatInputView.cs")} ";
            var expectedSubstring = $"check @{Path.Combine("src", "Tests", "MyChatInputHelper.cs")} ";
            prefixAndSubstring.Should().Equal(expectedPrefix, expectedSubstring);

            var subsequence = engine.GetCompletions("@cje");
            subsequence.Should().Equal($"@{Path.Combine("src", "Services", "CronJobExecutor.cs")} ");
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Theory]
    [InlineData("@", true)]
    [InlineData("check @xyz", true)]
    [InlineData("/he", false)]
    [InlineData("/session switch 4f", false)]
    [InlineData("plain text", false)]
    [InlineData("a @b c", false)]
    [InlineData("", false)]
    public void IsAtFileInput_ClassifiesInputModeCorrectly(string input, bool expected)
    {
        TypeaheadCompletionEngine.IsAtFileInput(input).Should().Be(expected);
    }
}
