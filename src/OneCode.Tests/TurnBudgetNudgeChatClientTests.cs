using Microsoft.Extensions.AI;
using NSubstitute;
using OneCode.Infrastructure.Agent;
using OneCode.Infrastructure.Ai;

namespace OneCode.Tests;

/// <summary>
/// 轮次预算收尾提醒装饰器的行为契约：80% 阈值注入、仅注入一次、小任务不注入、
/// 注入消息不回写调用方列表。
/// </summary>
public sealed class TurnBudgetNudgeChatClientTests
{
    // 阈值口径：ceil(maxTurns × 0.8)。maxTurns=15 → 第 12 次模型调用注入。
    private const int MaxTurns = 15;
    private const int NudgeAtCall = 12;

    private static List<ChatMessage> CreateMessages() => [new ChatMessage(ChatRole.User, "research the news")];

    private static IChatClient CreateCapturingInner(List<List<ChatMessage>> captured)
    {
        var inner = Substitute.For<IChatClient>();
        inner.GetResponseAsync(
                Arg.Do<IEnumerable<ChatMessage>>(m => captured.Add(m.ToList())),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(new ChatResponse()));
        inner.GetStreamingResponseAsync(
                Arg.Do<IEnumerable<ChatMessage>>(m => captured.Add(m.ToList())),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(_ => EmptyStream());
        return inner;
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> EmptyStream()
    {
        await Task.CompletedTask;
        yield break;
    }

    [Fact]
    public async Task GetResponseAsync_AtNudgeThreshold_InjectsReminderOnce()
    {
        var ct = TestContext.Current.CancellationToken;
        var captured = new List<List<ChatMessage>>();
        var sut = new TurnBudgetNudgeChatClient(CreateCapturingInner(captured), MaxTurns, new AgentPipelineMetrics());

        var callerMessages = CreateMessages();
        const int totalCalls = 14;
        for (var i = 0; i < totalCalls; i++)
            await sut.GetResponseAsync(callerMessages, cancellationToken: ct);

        captured.Should().HaveCount(totalCalls);
        var nudgedCalls = captured.Where(m => m.Any(msg =>
            msg.Role == ChatRole.User && msg.Text.Contains("[Turn budget]", StringComparison.Ordinal)));
        nudgedCalls.Should().HaveCount(1, "提醒必须在第 12 次调用注入且仅一次");
        captured[NudgeAtCall - 1].Should().HaveCount(callerMessages.Count + 1,
            "注入的请求比原始多一条消息");
        captured[^1].Should().HaveCount(callerMessages.Count,
            "注入之后后续调用不再追加");
        callerMessages.Should().HaveCount(1,
            "注入消息只属于当次请求，不得回写调用方列表");
    }

    [Fact]
    public async Task GetStreamingResponseAsync_AtNudgeThreshold_InjectsReminderOnce()
    {
        var ct = TestContext.Current.CancellationToken;
        var captured = new List<List<ChatMessage>>();
        var sut = new TurnBudgetNudgeChatClient(CreateCapturingInner(captured), MaxTurns, new AgentPipelineMetrics());

        const int totalCalls = 13;
        for (var i = 0; i < totalCalls; i++)
            await foreach (var _ in sut.GetStreamingResponseAsync(CreateMessages(), cancellationToken: ct)) { }

        captured.Should().HaveCount(totalCalls);
        captured[NudgeAtCall - 1].Should().HaveCount(2, "流式路径与同步路径同阈值注入");
        captured.Count(m => m.Count == 2).Should().Be(1, "流式路径同样只注入一次");
    }

    [Fact]
    public async Task GetResponseAsync_SmallTurnBudget_NeverInjects()
    {
        // maxTurns ≤ 5：提醒本身消耗预算，得不偿失——即便跑到上限也不注入。
        var ct = TestContext.Current.CancellationToken;
        var captured = new List<List<ChatMessage>>();
        var sut = new TurnBudgetNudgeChatClient(CreateCapturingInner(captured), maxTurns: 5, new AgentPipelineMetrics());

        const int totalCalls = 6;
        for (var i = 0; i < totalCalls; i++)
            await sut.GetResponseAsync(CreateMessages(), cancellationToken: ct);

        captured.Should().HaveCount(totalCalls);
        captured.Should().OnlyContain(m => m.Count == 1, "小任务全程不注入提醒");
    }

    [Fact]
    public async Task GetResponseAsync_CountsEveryModelCallIncludingStreaming()
    {
        // 小预算不注入提醒，但计数照常：轮数口径与是否提醒无关。
        var ct = TestContext.Current.CancellationToken;
        var metrics = new AgentPipelineMetrics();
        var sut = new TurnBudgetNudgeChatClient(CreateCapturingInner([]), maxTurns: 3, metrics);

        await sut.GetResponseAsync(CreateMessages(), cancellationToken: ct);
        await sut.GetResponseAsync(CreateMessages(), cancellationToken: ct);
        await foreach (var _ in sut.GetStreamingResponseAsync(CreateMessages(), cancellationToken: ct)) { }

        metrics.ModelCallCount.Should().Be(3);
    }

    [Fact]
    public async Task GetResponseAsync_BelowThreshold_PassesThroughUntouched()
    {
        var ct = TestContext.Current.CancellationToken;
        var captured = new List<List<ChatMessage>>();
        var sut = new TurnBudgetNudgeChatClient(CreateCapturingInner(captured), MaxTurns, new AgentPipelineMetrics());

        for (var i = 0; i < NudgeAtCall - 1; i++)
            await sut.GetResponseAsync(CreateMessages(), cancellationToken: ct);

        captured.Should().HaveCount(NudgeAtCall - 1);
        captured.Should().OnlyContain(m => m.Count == 1, "阈值之前不得提前注入");
        captured.Should().OnlyContain(m => m[0].Text == "research the news", "消息内容原样透传");
    }
}
