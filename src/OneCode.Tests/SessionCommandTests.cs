using OneCode.App.Commands;
using OneCode.App.Session;
using OneCode.Core.Commands;
using OneCode.Core.Domain;
using OneCode.Core.Session;
using NSubstitute;

namespace OneCode.Tests;

/// <summary>
/// /session 命令行为：switch 前缀解析、delete 两步确认与活跃会话守卫、list 短 id 输出。
/// </summary>
public sealed class SessionCommandTests
{
    private static readonly string FullIdA = "4f8a90c1e0d244ef8dba35b2588c0424";
    private static readonly string FullIdB = "4f8a90c1ffffffffffffffffffffffff";
    private static readonly string FullIdC = "1a2b3c4d5e6f7a889900112233445566";

    private readonly ISessionManager _manager = Substitute.For<ISessionManager>();
    private readonly ISessionEventStore _eventStore = Substitute.For<ISessionEventStore>();

    private SessionCommand CreateCommand() =>
        new(_manager, _eventStore, new NewCommand(_manager));

    private static ConversationSummary Summary(string fullId, string title, string mode = "plan") =>
        new(new SessionId(fullId), title, "gpt-5", 7, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, mode);

    private void SetupSessions(params ConversationSummary[] summaries)
    {
        _manager.ListAsync(Arg.Any<CancellationToken>()).Returns(summaries);
        _manager.ForegroundConversation.Returns((Conversation?)null);
        _manager.BackgroundSessions.Returns([]);
    }

    // ── switch 前缀解析 ──

    [Fact]
    public async Task SwitchSession_UniquePrefix_SwitchesWithFullId()
    {
        var ct = TestContext.Current.CancellationToken;
        SetupSessions(Summary(FullIdA, "修复会话恢复"));
        var conv = new Conversation { Id = new SessionId(FullIdA) };
        _manager.SwitchToSessionAsync(FullIdA, ct).Returns(conv);

        var result = await CreateCommand().ExecuteAsync(["switch", "4f8a"], ct);

        result.Should().BeOfType<CommandResult.TextResult>()
            .Which.Value.Should().Contain("4f8a90c1");
        await _manager.Received(1).SwitchToSessionAsync(FullIdA, ct);
    }

    [Fact]
    public async Task SwitchSession_FullId_PassesThroughDirectly()
    {
        var ct = TestContext.Current.CancellationToken;
        SetupSessions(Summary(FullIdA, "修复会话恢复"));
        _manager.SwitchToSessionAsync(FullIdA, ct).Returns(new Conversation { Id = new SessionId(FullIdA) });

        var result = await CreateCommand().ExecuteAsync(["switch", FullIdA], ct);

        result.Should().BeOfType<CommandResult.TextResult>();
        await _manager.Received(1).SwitchToSessionAsync(FullIdA, ct);
    }

    [Fact]
    public async Task SwitchSession_AmbiguousPrefix_ListsCandidatesWithoutSwitching()
    {
        var ct = TestContext.Current.CancellationToken;
        SetupSessions(Summary(FullIdA, "第一个会话"), Summary(FullIdB, "第二个会话"), Summary(FullIdC, "别的会话"));

        var result = await CreateCommand().ExecuteAsync(["switch", "4f8a"], ct);

        result.Should().BeOfType<CommandResult.ErrorResult>()
            .Which.Message.Should().Contain("4f8a90c1").And.Contain("Ambiguous");
        // 歧义时不得盲切
        await _manager.DidNotReceive().SwitchToSessionAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SwitchSession_NoMatch_ReturnsNotFound()
    {
        var ct = TestContext.Current.CancellationToken;
        SetupSessions(Summary(FullIdA, "修复会话恢复"));

        var result = await CreateCommand().ExecuteAsync(["switch", "deadbeef"], ct);

        result.Should().BeOfType<CommandResult.ErrorResult>()
            .Which.Message.Should().Contain("not found");
        await _manager.DidNotReceive().SwitchToSessionAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // ── delete ──

    [Fact]
    public async Task DeleteSession_WithoutForce_PromptsAndDoesNotDelete()
    {
        var ct = TestContext.Current.CancellationToken;
        SetupSessions(Summary(FullIdA, "要删的会话"));

        var result = await CreateCommand().ExecuteAsync(["delete", "4f8a"], ct);

        result.Should().BeOfType<CommandResult.TextResult>()
            .Which.Value.Should().Contain("要删的会话").And.Contain("--force");
        await _eventStore.DidNotReceive().DeleteAsync(Arg.Any<SessionId>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeleteSession_WithForce_DeletesEventLog()
    {
        var ct = TestContext.Current.CancellationToken;
        SetupSessions(Summary(FullIdA, "要删的会话"));

        var result = await CreateCommand().ExecuteAsync(["delete", "4f8a", "--force"], ct);

        result.Should().BeOfType<CommandResult.TextResult>()
            .Which.Value.Should().Contain("Deleted session");
        await _eventStore.Received(1).DeleteAsync(
            Arg.Is<SessionId>(id => id.Value == FullIdA), ct);
    }

    [Fact]
    public async Task DeleteSession_ForegroundActive_Rejects()
    {
        var ct = TestContext.Current.CancellationToken;
        SetupSessions(Summary(FullIdA, "前台会话"));
        _manager.ForegroundConversation.Returns(new Conversation { Id = new SessionId(FullIdA) });

        var result = await CreateCommand().ExecuteAsync(["delete", FullIdA, "--force"], ct);

        result.Should().BeOfType<CommandResult.ErrorResult>()
            .Which.Message.Should().Contain("foreground");
        await _eventStore.DidNotReceive().DeleteAsync(Arg.Any<SessionId>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeleteSession_BackgroundActive_Rejects()
    {
        var ct = TestContext.Current.CancellationToken;
        SetupSessions(Summary(FullIdA, "后台会话"));
        _manager.BackgroundSessions.Returns(
            [new BackgroundSession(new Conversation { Id = new SessionId(FullIdA) })]);

        var result = await CreateCommand().ExecuteAsync(["delete", FullIdA, "--force"], ct);

        result.Should().BeOfType<CommandResult.ErrorResult>()
            .Which.Message.Should().Contain("background");
        await _eventStore.DidNotReceive().DeleteAsync(Arg.Any<SessionId>(), Arg.Any<CancellationToken>());
    }

    // ── list ──

    [Fact]
    public async Task ListSessions_ShowsShortIdNotFullId()
    {
        var ct = TestContext.Current.CancellationToken;
        SetupSessions(Summary(FullIdA, "修复会话恢复", "build"));

        var result = await CreateCommand().ExecuteAsync(["list"], ct);

        var text = result.Should().BeOfType<CommandResult.TextResult>().Which.Value;
        text.Should().Contain(FullIdA[..8]);
        text.Should().NotContain(FullIdA, "完整 32 位 id 不应再出现在列表中");
    }
}
