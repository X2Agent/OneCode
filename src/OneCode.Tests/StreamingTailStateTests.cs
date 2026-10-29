using OneCode.WebUI.Services;

namespace OneCode.Tests;

/// <summary>
/// 流式尾段字级错峰状态机契约：新增字符获得同一增量内 0..n 的错峰索引；
/// 已展示字符跨渲染保持 key（Blazor 按 key 复用 DOM，动画不被重建打断）；
/// 错峰索引封顶 16；窗口（600 字符）外收敛为纯文本；块轮转不重播已展示字符。
/// </summary>
public sealed class StreamingTailStateTests
{
    private static WebMessage NewStreamingMessage() =>
        new(WebRole.Assistant, string.Empty) { IsStreaming = true };

    [Fact]
    public void Plan_FirstDelta_SpansWholeTailWithSequentialIndexes()
    {
        var state = new StreamingTailState();

        var plan = state.Plan(NewStreamingMessage(), tailStart: 7, tail: "你好");

        plan.StableText.Should().BeEmpty();
        plan.Spans.Should().Equal(
        [
            new TailSpan(7, 0, "你"),
            new TailSpan(8, 1, "好"),
        ]);
    }

    [Fact]
    public void Plan_SecondDelta_OldSpansKeepKeysAndNewCharsAppend()
    {
        var state = new StreamingTailState();
        var message = NewStreamingMessage();
        state.Plan(message, 0, "你好");

        var plan = state.Plan(message, 0, "你好世界");

        // 窗口内的旧 span 键与错峰索引保持不变（Blazor 按 key 复用 DOM，动画不被打断），新字符追加增量索引。
        plan.StableText.Should().BeEmpty();
        plan.Spans.Should().Equal(
        [
            new TailSpan(0, 0, "你"),
            new TailSpan(1, 1, "好"),
            new TailSpan(2, 0, "世"),
            new TailSpan(3, 1, "界"),
        ]);
    }

    [Fact]
    public void Plan_LargeDelta_StaggerIndexCappedAtLimit()
    {
        var state = new StreamingTailState();
        var tail = new string('你', 20);

        var plan = state.Plan(NewStreamingMessage(), 0, tail);

        plan.Spans.Should().HaveCount(20);
        plan.Spans.Take(16).Should().OnlyContain(s => s.Index <= 15);
        plan.Spans.Skip(16).Should().OnlyContain(s => s.Index == StreamingTailState.MaxStagger);
    }

    [Fact]
    public void Plan_BacklogWindow_ConvergesOldSpansToStableText()
    {
        var state = new StreamingTailState();
        var tail = new string('你', 610);

        var plan = state.Plan(NewStreamingMessage(), 0, tail);

        // 窗口 600：前 10 字符收敛为纯文本，剩余 600 个 span 从偏移 10 起。
        plan.StableText.Length.Should().Be(10);
        plan.Spans.Should().HaveCount(600);
        plan.Spans[0].Offset.Should().Be(10);
    }

    [Fact]
    public void Plan_BlockRotation_DoesNotRespanShownChars()
    {
        var state = new StreamingTailState();
        var message = NewStreamingMessage();
        state.Plan(message, tailStart: 7, tail: "你好世界");

        // 块轮转：已展示字符转入 head，新尾段从零错峰，不得重播旧字符。
        var plan = state.Plan(message, tailStart: 13, tail: "新段");

        plan.StableText.Should().BeEmpty("已展示字符已随轮转进入 head，新尾段整体是本轮增量");
        plan.Spans.Should().Equal(
        [
            new TailSpan(13, 0, "新"),
            new TailSpan(14, 1, "段"),
        ]);
    }

    [Fact]
    public void Plan_OwnerSwitch_StateResets()
    {
        var state = new StreamingTailState();
        state.Plan(NewStreamingMessage(), 0, "你好");

        var fresh = state.Plan(NewStreamingMessage(), 0, "你好");

        fresh.StableText.Should().BeEmpty("换流式消息后尾段必须整体重新错峰");
        fresh.Spans.Should().HaveCount(2);
        fresh.Spans[0].Index.Should().Be(0);
    }

    [Fact]
    public void Plan_MarkShownPath_FollowingDeltaStartsFromNewChars()
    {
        var state = new StreamingTailState();
        var message = NewStreamingMessage();
        state.Plan(message, 0, "你好");
        state.MarkShown(message, 2); // 整段 Markdown 渲染路径：全部视为已展示

        var plan = state.Plan(message, 0, "你好abc新");

        plan.StableText.Should().Be("你好");
        // token 用首字符的错峰索引：abc 一词占 0，新字的索引按其在增量内的字符位置为 3。
        plan.Spans.Should().Equal(
        [
            new TailSpan(2, 0, "abc"),
            new TailSpan(5, 3, "新"),
        ]);
    }

    [Fact]
    public void Reset_SessionSwitch_DropsAllState()
    {
        var state = new StreamingTailState();
        var message = NewStreamingMessage();
        state.Plan(message, 0, "你好世界");
        state.Reset();

        var plan = state.Plan(message, 0, "你好世界");

        plan.StableText.Should().BeEmpty("会话切换复位后旧偏移状态不得残留");
        plan.Spans.Should().HaveCount(4);
        plan.Spans[0].Should().Be(new TailSpan(0, 0, "你"));
    }
}
