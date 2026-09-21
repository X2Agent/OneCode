using Microsoft.Agents.AI.Compaction;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using OneCode.Infrastructure.Agent;

namespace OneCode.Tests;

/// <summary>
/// A4 行为契约：空摘要与「比原文更长」的摘要不得替换原始消息。
/// </summary>
/// <remarks>
/// MAF 原生策略只在 LLM 调用抛错时恢复被排除的组。空响应会被写成
/// <c>[Summary unavailable]</c> 占位符并提交，超长摘要也会照常插入——
/// 两种情况下原文都被销毁，而摘要没有提供等价信息或没有真正省下预算。
/// </remarks>
public sealed class GuardedSummarizationCompactionStrategyTests
{
    [Fact]
    public async Task EmptySummary_LeavesOriginalMessagesInPlace()
    {
        var client = Substitute.For<IChatClient>();
        client.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, "   ")));

        var index = BuildIndex(messageCount: 30, messageChars: 400);

        var changed = await RunAsync(client, index);

        changed.Should().BeFalse("an empty summary must not replace the messages it was meant to condense");
        index.Groups.Should().NotContain(g => g.Kind == CompactionGroupKind.Summary);
        index.Groups.Count(g => g.IsExcluded).Should().Be(0, "a rejected summary must roll the index back");
    }

    /// <summary>
    /// 反证：摘要比被替换内容更长时同样不提交——压缩不能反过来放大上下文。
    /// </summary>
    [Fact]
    public async Task OversizedSummary_LeavesOriginalMessagesInPlace()
    {
        var client = Substitute.For<IChatClient>();
        client.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, new string('x', 200_000))));

        var index = BuildIndex(messageCount: 30, messageChars: 200);

        var changed = await RunAsync(client, index);

        changed.Should().BeFalse("a summary larger than the content it replaces defeats compaction");
        index.Groups.Should().NotContain(g => g.Kind == CompactionGroupKind.Summary);
        index.Groups.Count(g => g.IsExcluded).Should().Be(0);
    }

    /// <summary>
    /// 正常路径不得被守卫误伤：更短且非空的摘要必须被接受。
    /// </summary>
    [Fact]
    public async Task ValidSummary_IsAccepted()
    {
        var client = Substitute.For<IChatClient>();
        client.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Short work summary.")));

        var index = BuildIndex(messageCount: 30, messageChars: 400);

        var changed = await RunAsync(client, index);

        changed.Should().BeTrue();
        index.Groups.Should().Contain(g => g.Kind == CompactionGroupKind.Summary);
        index.IncludedByteCount.Should().BeLessThan(30 * 400, "the accepted summary must actually shrink the context");
    }

    /// <summary>
    /// 反证：比较基准必须是「所有被替换分组的字节总量」，而不是只算其中一个分组。
    /// </summary>
    /// <remarks>
    /// MAF 把摘要分组插在<b>首个被摘要分组的位置</b>上，其后每个分组整体后移一位。若按列表位置
    /// 对齐前后快照，第 i 个位置上的分组会被拿去和原第 i+1 个分组的排除状态比较，结果是除最后一个
    /// 之外的所有被替换分组都不计入 replacedBytes。此时一个真正省下预算的摘要会被误判为
    /// 「没有变小」而被回滚——压缩静默失效，且失败方向不可见（只在长会话里表现为上下文不降）。
    /// </remarks>
    [Fact]
    public async Task Summary_IsComparedAgainstEveryReplacedGroupNotJustTheLastOne()
    {
        var index = BuildIndex(messageCount: 30, messageChars: 400);
        var groupBytes = index.Groups.Select(group => group.ByteCount).ToList();
        groupBytes.Should().HaveCountGreaterThan(2, "the fixture must be able to replace several groups");

        // 比「单个被替换分组」大，但远小于「全部被替换分组之和」：
        // 只有把每个被替换分组都计入，这个摘要才会被接受。
        var summaryChars = groupBytes.Max() + 500;
        var client = Substitute.For<IChatClient>();
        client.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, new string('s', summaryChars))));

        var beforeBytes = index.IncludedByteCount;

        // 目标设为「token 降到 1000 以下」：策略会先排除多个分组才满足目标。
        // 这正是位置对齐会算错的形状——只排除一个分组时，两种实现没有区别。
        var changed = await RunAsync(client, index, target: CompactionTriggers.TokensBelow(1_000));

        var replaced = index.Groups.Where(group => group.IsExcluded).ToList();
        replaced.Should().HaveCountGreaterThan(1,
            "the fixture must replace several groups, otherwise the positional mismatch cannot be exercised");
        replaced.Sum(group => group.ByteCount).Should().BeGreaterThan(summaryChars,
            "the summary must be shorter than the total it replaces, otherwise this test proves nothing");
        changed.Should().BeTrue(
            "a summary shorter than the total it replaces must be accepted even when it is longer than one group");
        index.IncludedByteCount.Should().BeLessThan(beforeBytes, "the accepted summary must actually shrink the context");
    }

    /// <summary>
    /// 取消必须原样传播：它既不是「摘要失败」，也不得被当作可回滚的普通错误吞掉。
    /// </summary>
    [Fact]
    public async Task Cancelled_Propagates()
    {
        var client = Substitute.For<IChatClient>();
        client.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns<ChatResponse>(_ => throw new OperationCanceledException());

        var index = BuildIndex(messageCount: 30, messageChars: 400);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var act = () => RunAsync(client, index, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    // Helpers

    private static async Task<bool> RunAsync(
        IChatClient client,
        CompactionMessageIndex index,
        CancellationToken ct = default,
        CompactionTrigger? target = null)
    {
        var trigger = CompactionTriggers.TokensExceed(1);
        var inner = new SummarizationCompactionStrategy(
            client, trigger, minimumPreservedGroups: 8, summarizationPrompt: "summarize", target);
        var sut = new GuardedSummarizationCompactionStrategy(inner, trigger, target);

        return await sut.CompactAsync(index, NullLogger.Instance, ct);
    }

    private static CompactionMessageIndex BuildIndex(int messageCount, int messageChars)
    {
        List<ChatMessage> messages = [];
        for (var i = 0; i < messageCount; i++)
        {
            messages.Add(new ChatMessage(
                i % 2 == 0 ? ChatRole.User : ChatRole.Assistant,
                new string('a', messageChars)));
        }

        var create = typeof(CompactionMessageIndex).GetMethod(
            "Create",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)!;
        return (CompactionMessageIndex)create.Invoke(null, [messages, null])!;
    }
}
