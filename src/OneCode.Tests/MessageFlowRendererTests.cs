using OneCode.Tui;

namespace OneCode.Tests;

public sealed class MessageFlowRendererTests
{
    [Fact]
    public void RenderUserMessage_HeaderRightAlignsTimestampWithScrollbarGap()
    {
        const int width = 80;
        var renderer = new MessageFlowRenderer { CurrentWidth = width };
        var message = new UserMessage(
            "user-message",
            "hello",
            new DateTimeOffset(2026, 7, 28, 12, 30, 0, TimeSpan.Zero));
        var expectedTime = message.Timestamp.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);

        var rendered = renderer.RenderMessage(message);
        rendered.Should().ContainSingle();
        var header = rendered[0].FullText;

        header.Should().HaveLength(width);
        header.Should().Contain("hello");
        header.Should().EndWith(expectedTime);
        header.Should().NotContain(Environment.UserName);
    }

    [Fact]
    public void RenderToolResultMessage_MixedText_DecodesUnicodeEscapes()
    {
        var renderer = new MessageFlowRenderer { CurrentWidth = 80 };
        var message = new ToolResultMessage(
            "tool-result",
            "call-1",
            "Read",
            "状态：\\u8bfb\\u53d6\\u6210\\u529f",
            false,
            DateTimeOffset.UtcNow);

        var text = string.Join("\n", renderer.RenderMessage(message).Select(line => line.FullText));

        text.Should().Contain("状态：读取成功");
        text.Should().NotContain("\\u8bfb");
    }

    [Fact]
    public void TrimReplyLeadingWhitespace_LeadingSpacesAndTabs_AreRemoved()
    {
        // 部分模型在 reasoning/text 交错的流式输出里给文本段带上大段前导缩进，
        // 原样渲染会让整段看起来居中——回复开头的前导空白必须剥掉。
        MessageFlowRenderer.TrimReplyLeadingWhitespace("                    再补两个检索")
            .Should().Be("再补两个检索");
        MessageFlowRenderer.TrimReplyLeadingWhitespace("\t\t标题")
            .Should().Be("标题");
    }

    [Fact]
    public void TrimReplyLeadingWhitespace_AllWhitespace_BecomesEmpty()
    {
        // 剥完为空的段落（纯前导空白）由调用方直接丢弃，不产生空行。
        MessageFlowRenderer.TrimReplyLeadingWhitespace("    \t  ").Should().BeEmpty();
    }

    [Fact]
    public void TrimReplyLeadingWhitespace_InteriorIndentation_IsPreserved()
    {
        // 只处理回复开头：正文与代码块内部的缩进有语义，不动。
        const string text = "  def foo():\n      return 1";

        MessageFlowRenderer.TrimReplyLeadingWhitespace(text)
            .Should().Be("def foo():\n      return 1");
    }

    [Fact]
    public void MakeCompletedToolLine_GroupWithEarlyFailureAndTrailingSuccess_ExtractsFailureSummary()
    {
        IReadOnlyList<ToolGroupEntry> group =
        [
            new("""{"url":"https://bad.invalid"}""", """{"content":"[ERROR] DNS resolution failed","isError":true}""", "120ms", IsError: true),
            new("""{"url":"https://ok.example"}""", """{"content":"200 OK body","isError":false}""", "80ms", IsError: false),
        ];

        // 末尾成员是成功的，因此传入的 result 为成功输出、isError 为 false
        var line = ConversationRenderer.MakeCompletedToolLine(
            "WebFetch",
            isError: false,
            toolInput: """{"url":"https://ok.example"}""",
            duration: "200ms",
            result: """{"content":"200 OK body","isError":false}""",
            maxWidth: 120,
            groupCount: 2,
            group: group);

        line.FullText.Should().Contain("1/2 失败: DNS resolution failed",
            "组行摘要应从失败成员提取错误首行，而不是误取末尾成功成员的正常输出");
        line.FullText.Should().NotContain("200 OK body");
    }

    [Fact]
    public void StreamingToolTracker_GroupTotalElapsed_FreezesMemberDurationOnCompletion()
    {
        var tracker = new StreamingToolTracker();
        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        var oneSecondAgo = now - System.Diagnostics.Stopwatch.Frequency;
        var halfSecondAgo = now - (System.Diagnostics.Stopwatch.Frequency / 2);

        tracker.RegisterGroup("WebSearch|", lineIndex: 0, args: "q1", result: "r1", startTick: oneSecondAgo, isError: false);
        var appended = tracker.TryAppendToGroup(
            "WebSearch|", args: "q2", result: "r2", startTick: halfSecondAgo, isError: false,
            out _, out var members, out _);

        appended.Should().BeTrue();
        members.Should().HaveCount(2);
        var firstMemberDuration = members[0].Duration;

        // 模拟后续第 3 条调用在 20ms 后并入，第 1 条已完成成员的耗时必须保持固化不变
        Thread.Sleep(25);
        tracker.TryAppendToGroup(
            "WebSearch|", args: "q3", result: "r3", startTick: System.Diagnostics.Stopwatch.GetTimestamp(), isError: false,
            out _, out var membersAfterWait, out _);

        membersAfterWait[0].Duration.Should().Be(firstMemberDuration,
            "已完成入组的成员耗时应在完成瞬间固化，不应随后续时间推移继续增长");
    }

    [Fact]
    public void RenderToolRunSegment_ThinkingOrTextBetweenSameNameTools_FlushesRunAndPreservesOrder()
    {
        var renderer = new MessageFlowRenderer { CurrentWidth = 100 };
        var ts = DateTimeOffset.UtcNow;

        var assistant1 = new AssistantMessage(
            "a1",
            [new ToolUseBlock("call-1", "WebSearch", """{"query":"first"}""")],
            ts);
        var result1 = new ToolResultMessage("r1", "call-1", "WebSearch", "result 1", false, ts);

        var assistant2 = new AssistantMessage(
            "a2",
            [
                new ThinkingBlock("thinking between tools"),
                new ToolUseBlock("call-2", "WebSearch", """{"query":"second"}"""),
            ],
            ts);
        var result2 = new ToolResultMessage("r2", "call-2", "WebSearch", "result 2", false, ts);

        var segment = new MessageFlowRenderer.ToolRunSegment
        {
            Assistants = [assistant1, assistant2],
            Results = [result1, result2],
        };
        var lines = renderer.RenderToolRunSegment(segment);

        // 两个 WebSearch 之间夹了 ThinkingBlock，不得跨思考块合并成 ×2，且顺序必须是 call-1 -> Thinking -> call-2
        lines.Should().HaveCount(3);
        lines[0].FullText.Should().Contain("WebSearch").And.Contain("first").And.NotContain("×2");
        lines[1].FullText.Should().Contain("Thought");
        lines[2].FullText.Should().Contain("WebSearch").And.Contain("second").And.NotContain("×2");
    }
}
