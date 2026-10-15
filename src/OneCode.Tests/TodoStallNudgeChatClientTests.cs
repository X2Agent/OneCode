using Microsoft.Extensions.AI;
using NSubstitute;
using OneCode.Infrastructure.Ai;

namespace OneCode.Tests;

/// <summary>
/// 待办停滞提醒：自上次待办变更起累计 N 次工具调用仍未勾选即持续提醒，
/// 勾选后计数清零；注入消息只属于当次请求。
/// </summary>
public sealed class TodoStallNudgeChatClientTests
{
    private const int Threshold = 3;
    private const string NudgeMarker = "[Todo progress]";

    private static IChatClient CreateCapturingInner(List<List<ChatMessage>> captured)
    {
        var inner = Substitute.For<IChatClient>();
        inner.GetResponseAsync(
                Arg.Do<IEnumerable<ChatMessage>>(m => captured.Add(m.ToList())),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(new ChatResponse()));
        return inner;
    }

    private static ChatMessage ToolCall(string toolName) =>
        new(ChatRole.Assistant, [new FunctionCallContent(Guid.NewGuid().ToString("N"), toolName)]);

    private static List<ChatMessage> History(params string[] toolNames) =>
        [new(ChatRole.User, "run the plan"), .. toolNames.Select(ToolCall)];

    private static bool IsNudged(IEnumerable<ChatMessage> request) =>
        request.Any(message => message.Role == ChatRole.User && message.Text.Contains(NudgeMarker, StringComparison.Ordinal));

    [Fact]
    public async Task GetResponseAsync_BelowThreshold_DoesNotInject()
    {
        var captured = new List<List<ChatMessage>>();
        var sut = new TodoStallNudgeChatClient(CreateCapturingInner(captured), Threshold);

        await sut.GetResponseAsync(History("WebSearch", "WebFetch"), cancellationToken: TestContext.Current.CancellationToken);

        IsNudged(captured.Single()).Should().BeFalse("two calls since the start is below the threshold of three");
    }

    [Fact]
    public async Task GetResponseAsync_AtOrAboveThreshold_PersistentlyInjectsTransientNudge_WithoutMutatingCallerList()
    {
        var captured = new List<List<ChatMessage>>();
        var sut = new TodoStallNudgeChatClient(CreateCapturingInner(captured), Threshold);
        var ct = TestContext.Current.CancellationToken;

        var atThreshold = History("WebSearch", "WebFetch", "Read");
        var furtherStalled = History("WebSearch", "WebFetch", "Read", "Edit");
        await sut.GetResponseAsync(atThreshold, cancellationToken: ct);
        await sut.GetResponseAsync(furtherStalled, cancellationToken: ct);

        IsNudged(captured[0]).Should().BeTrue("the third call since the last todo update reaches the threshold");
        captured[0].Should().HaveCount(atThreshold.Count + 1);
        IsNudged(captured[1]).Should().BeTrue("while the todo remains uncompleted past the threshold, the transient reminder must stay active");
        captured[1].Count(m => m.Role == ChatRole.User && m.Text.Contains(NudgeMarker, StringComparison.Ordinal))
            .Should().Be(1, "transient injection must never stack multiple reminders in a single request");
        atThreshold.Should().HaveCount(4, "the reminder belongs to one request only and must not be written back");
    }

    [Fact]
    public async Task GetResponseAsync_TodoMutation_ResetsCountAndRearmsNudge()
    {
        var captured = new List<List<ChatMessage>>();
        var sut = new TodoStallNudgeChatClient(CreateCapturingInner(captured), Threshold);
        var ct = TestContext.Current.CancellationToken;

        await sut.GetResponseAsync(History("Read", "Read", "Read"), cancellationToken: ct);
        // 勾选后只剩 2 次调用：桶归零，不提醒。
        await sut.GetResponseAsync(History("Read", "Read", "Read", "todos_complete", "Read", "Read"), cancellationToken: ct);
        // 勾选之后又累计 3 次：重新装填后的第一次停滞。
        await sut.GetResponseAsync(History("Read", "Read", "Read", "todos_complete", "Read", "Read", "Read"), cancellationToken: ct);

        IsNudged(captured[0]).Should().BeTrue();
        IsNudged(captured[1]).Should().BeFalse("calls before the todos_complete do not count toward the new stall");
        IsNudged(captured[2]).Should().BeTrue("a new stall after ticking a step must nudge again");
    }

    [Fact]
    public async Task GetResponseAsync_ReadOnlyTodoCalls_CountAsStall()
    {
        var captured = new List<List<ChatMessage>>();
        var sut = new TodoStallNudgeChatClient(CreateCapturingInner(captured), Threshold);

        // todos_get_* 只是查看清单，不算进展。
        await sut.GetResponseAsync(
            History("todos_get_remaining", "todos_get_all", "todos_get_remaining"),
            cancellationToken: TestContext.Current.CancellationToken);

        IsNudged(captured.Single()).Should().BeTrue("reading the checklist is not progress on it");
    }
}
