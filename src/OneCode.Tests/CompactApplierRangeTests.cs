using OneCode.App.Services.Compact;
using OneCode.Core.Domain;

namespace OneCode.Tests;

/// <summary>
/// A2 行为契约：Partial compact 只替换它自己的区间。
/// </summary>
/// <remarks>
/// 修复前 <c>ApplyPartialCompact</c> 在重建历史后仍调用一个按消息条数裁剪的 cleanup
/// （<c>while (Count &gt; 3 + RecentMessagesToKeep) RemoveAt(3)</c>），于是区间**外**的消息
/// 会被静默删除，违反「Partial 保留区间前后内容」的契约。
/// </remarks>
public sealed class CompactApplierRangeTests
{
    [Fact]
    public void ApplyPartialCompact_PreservesMessagesOutsideRange()
    {
        var session = CreateSession(20);
        var prefixIds = session.Messages.Take(4).Select(m => m.Id).ToList();
        var suffixIds = session.Messages.Skip(14).Select(m => m.Id).ToList();

        new CompactApplier().ApplyPartialCompact(session, "range summary", fromIndex: 4, upToIndex: 14);

        session.Messages.Take(prefixIds.Count).Select(m => m.Id)
            .Should().Equal(prefixIds, "messages before the range must survive verbatim");
        session.Messages.TakeLast(suffixIds.Count).Select(m => m.Id)
            .Should().Equal(suffixIds, "messages after the range must survive verbatim");
    }

    /// <summary>
    /// 反证：长前缀不得被条数裁剪吃掉。旧 cleanup 会把历史压到 3 + RecentMessagesToKeep 条，
    /// 因此当区间外的消息总数超过该上限时前缀必然受损。
    /// </summary>
    [Fact]
    public void ApplyPartialCompact_LongPrefix_IsNotTrimmedByCount()
    {
        var session = CreateSession(40);
        var prefixCount = 20;
        var prefixIds = session.Messages.Take(prefixCount).Select(m => m.Id).ToList();

        new CompactApplier().ApplyPartialCompact(session, "range summary", fromIndex: prefixCount, upToIndex: 30);

        session.Messages.Take(prefixCount).Select(m => m.Id)
            .Should().Equal(prefixIds,
                "a partial compact owns only its range; trimming by message count would delete outside it");
    }

    /// <summary>Full compact 仍保留尾部 RecentMessagesToKeep 条。</summary>
    [Fact]
    public void ApplyFullCompact_RetainsRecentTail()
    {
        var session = CreateSession(20);
        var tailIds = session.Messages.TakeLast(CompactConstants.RecentMessagesToKeep)
            .Select(m => m.Id).ToList();

        new CompactApplier().ApplyFullCompact(session, "full summary");

        // 3 个前缀消息（边界标记 + 用户标记 + 摘要）之后就是保留的尾部。
        session.Messages.Skip(3).Select(m => m.Id).Should().Equal(tailIds);
    }

    /// <summary>
    /// A2 契约前提：<c>AdjustRangeToAtomicBoundaries</c> 必须幂等。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>CompactService</c> 在送摘要前规范化一次区间，<c>CompactApplier.ApplyPartialCompact</c>
    /// 在重建历史时又规范化一次。第二次调用之所以安全，只因为已对齐的边界恰好落在分组边缘上、
    /// 会被原样返回。这个前提此前没有任何用例守着：一旦 helper 改成「向外扩张到最近的工具组」这类
    /// 非幂等实现，applier 的第二次调用就会静默替换掉模型从未摘要过的消息，而区间内的用例仍会通过。
    /// </para>
    /// <para>
    /// 反证：若 helper 不幂等（例如把 <c>from</c> 一律吸附到下一个组起点），本用例失败。
    /// </para>
    /// </remarks>
    [Fact]
    public void AdjustRangeToAtomicBoundaries_IsIdempotent()
    {
        var messages = CreateToolBatchMessages();
        var groups = MessageApiInvariantHelper.GetAtomicGroups(messages);

        // 覆盖所有可能被调用方选中的边界：区间内部、组边缘、越界。
        foreach (var from in Enumerable.Range(0, messages.Count + 1))
        {
            foreach (var to in Enumerable.Range(from, messages.Count + 1 - from))
            {
                var once = MessageApiInvariantHelper.AdjustRangeToAtomicBoundaries(messages, from, to);
                var twice = MessageApiInvariantHelper.AdjustRangeToAtomicBoundaries(
                    messages, once.FromIndex, once.UpToIndex);

                twice.Should().Be(once,
                    $"re-normalising the already-normalised range [{once.FromIndex}, {once.UpToIndex}) " +
                    $"derived from [{from}, {to}) must not move a boundary; otherwise the applier's " +
                    "second adjustment would replace messages the summariser never saw");
            }
        }

        // 前提校验：样本必须真的含有多消息原子组，否则上面只是在对单消息分组做恒等断言。
        groups.Should().Contain(g => g.End - g.Start > 1,
            "the sample must contain an assistant tool call with its results, or idempotency is untested");
    }

    private static List<Message> CreateToolBatchMessages()
    {
        var now = DateTimeOffset.UtcNow;
        return
        [
            new UserMessage(Id: "u0", Content: "run the tool", Timestamp: now),
            new AssistantMessage(Id: "a1", Content:
            [
                new ToolUseBlock(Id: "call-1", Name: "Read", Input: """{"path":"a.cs"}"""),
                new ToolUseBlock(Id: "call-2", Name: "Grep", Input: """{"pattern":"x"}"""),
            ], Timestamp: now),
            new ToolResultMessage(Id: "r2", ToolUseId: "call-1", ToolName: "Read", Content: "a", IsError: false, Timestamp: now),
            new ToolResultMessage(Id: "r3", ToolUseId: "call-2", ToolName: "Grep", Content: "b", IsError: false, Timestamp: now),
            new UserMessage(Id: "u4", Content: "thanks", Timestamp: now),
            new AssistantMessage(Id: "a5", Content: [new TextBlock("done")], Timestamp: now),
            new UserMessage(Id: "u6", Content: "next", Timestamp: now),
        ];
    }

    private static Conversation CreateSession(int messageCount)
    {
        var session = new Conversation
        {
            Id = SessionId.NewId(),
            Name = "range-test",
            WorkingDirectory = Environment.CurrentDirectory,
            Model = "test-model",
        };

        for (var i = 0; i < messageCount; i++)
        {
            session.Messages.Add(i % 2 == 0
                ? new UserMessage(Id: $"u{i}", Content: $"user {i}", Timestamp: DateTimeOffset.UtcNow)
                : new AssistantMessage(Id: $"a{i}", Content: [new TextBlock($"assistant {i}")], Timestamp: DateTimeOffset.UtcNow));
        }

        return session;
    }
}
