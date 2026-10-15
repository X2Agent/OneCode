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
        new([], Directory.GetCurrentDirectory(), () => [], sessionCandidatesProvider: provider);

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
}
