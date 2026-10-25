using OneCode.App.Query;
using OneCode.Tui;

namespace OneCode.Tests;

public sealed class MessageRendererTests
{
    [Fact]
    public void BuildToolDetailLines_ExpandedResultRendersAllContent()
    {
        var result = string.Join("\n", Enumerable.Range(1, 30).Select(index => $"result-line-{index:D2}"));
        var tag = new ToolLineTag("Read", "{\"file_path\":\"sample.txt\"}", result, IsExpanded: true);

        var lines = MessageRenderer.BuildToolDetailLines(tag, viewportWidth: 80);
        var text = string.Join("\n", lines.Select(line => line.Text));

        text.Should().Contain("result-line-01");
        text.Should().Contain("result-line-30");
        text.Should().NotContain("more content available");
    }

    [Fact]
    public void BuildToolDetailLines_ExpandedArgumentsRenderAllWrappedSegments()
    {
        var longValue = new string('x', 240);
        var tag = new ToolLineTag("Bash", $"{{\"command\":\"{longValue}\"}}", null, IsExpanded: true);

        var lines = MessageRenderer.BuildToolDetailLines(tag, viewportWidth: 40);
        var renderedXCount = lines.Sum(line => line.Text.Count(ch => ch == 'x'));

        renderedXCount.Should().Be(240);
    }

    [Fact]
    public void BuildToolDetailLines_MixedResult_DecodesUnicodeEscapes()
    {
        var tag = new ToolLineTag(
            "Read",
            null,
            "状态：\\u8bfb\\u53d6\\u6210\\u529f",
            IsExpanded: true);

        var text = string.Join("\n", MessageRenderer.BuildToolDetailLines(tag, 80).Select(line => line.Text));

        text.Should().Contain("状态：读取成功");
        text.Should().NotContain("\\u8bfb");
    }

    [Fact]
    public void BuildToolDetailLines_ArgsWithEscapedNewline_RendersAsSeparateLines()
    {
        // 用户可见行为：多行 Bash 命令在展开详情里逐行显示，而不是挤在一行显示 "\n"。
        var tag = new ToolLineTag(
            "Bash",
            """{"command":"git status --short\ngit log --oneline -5"}""",
            null,
            IsExpanded: true);

        var texts = MessageRenderer.BuildToolDetailLines(tag, viewportWidth: 80)
            .Select(line => line.Text)
            .ToArray();

        texts.Should().Contain(line => line.Contains("git status --short", StringComparison.Ordinal));
        texts.Should().Contain(line => line.Contains("git log --oneline -5", StringComparison.Ordinal));
        texts.Should().NotContain(line => line.Contains("\\n", StringComparison.Ordinal));
    }

    [Fact]
    public void BuildToolDetailLines_ResultWithAnsiEscapes_RendersPlainText()
    {
        var tag = new ToolLineTag(
            "Bash",
            null,
            """{"output":"\u001b[31merror: build failed\u001b[0m"}""",
            IsExpanded: true);

        var text = string.Join("\n", MessageRenderer.BuildToolDetailLines(tag, 80).Select(line => line.Text));

        text.Should().Contain("error: build failed");
        text.Should().NotContain("\u001b");
        text.Should().NotContain("\\u001b");
    }

    [Fact]
    public void BuildToolDetailLines_WiderViewport_UsesFewerLinesWithoutLosingContent()
    {
        var content = string.Join("", Enumerable.Repeat("工具结果中文", 40));
        var tag = new ToolLineTag("Read", null, content, IsExpanded: true);

        var narrow = MessageRenderer.BuildToolDetailLines(tag, viewportWidth: 40);
        var wide = MessageRenderer.BuildToolDetailLines(tag, viewportWidth: 100);

        wide.Count.Should().BeLessThan(narrow.Count);
        string.Concat(narrow.Select(line => line.Text.Trim())).Should().Be(
            string.Concat(wide.Select(line => line.Text.Trim())));
    }

    [Fact]
    public void ReflowExpandedToolDetails_WidthChanges_RebuildsMaterializedDetailLines()
    {
        var content = string.Join("", Enumerable.Repeat("工具结果中文", 40));
        var tag = new ToolLineTag("Read", null, content, IsExpanded: true);
        var view = new MessageListView();
        view.AppendLines(new[]
        {
            FormattedLine.FromSegmentsWithTag(
                new[] { new LineSegment("▼ Read", TuiPalette.FgMuted) },
                tag),
        });

        view.ReflowExpandedToolDetails(40);
        var narrowLineCount = view.TotalLines;
        view.ReflowExpandedToolDetails(100);

        view.TotalLines.Should().BeLessThan(narrowLineCount);
        string.Concat(view.RenderedLines.Skip(1).Select(line => line.Trim())).Should().Be(content);
    }

    [Fact]
    public void BuildToolDetailLines_RunningSubagent_ShowsArgsAndLiveStats()
    {
        // 运行中的子代理行：Args（代理名 · 描述）+ 实时统计都可见，不再落到兜底文案。
        var tag = new ToolLineTag(
            "Agent",
            "Explore · 检索USDD脱锚与SunPerp",
            null,
            IsExpanded: true,
            LiveProgress: new SubagentProgressEvent(
                ForkId: "fork-1",
                AgentName: "Explore",
                OneCode.App.Query.SubagentProgressPhase.Running,
                ToolCallCount: 21,
                TurnCount: 21,
                CurrentToolName: "websearch",
                ElapsedMs: 95_100));

        var text = string.Join("\n", MessageRenderer.BuildToolDetailLines(tag, 80).Select(line => line.Text));

        text.Should().Contain("检索USDD脱锚与SunPerp", "运行中展开必须能看到任务目标");
        text.Should().Contain("运行中");
        text.Should().Contain("turn 21");
        text.Should().Contain("21 次工具调用");
        text.Should().Contain("websearch");
        text.Should().Contain("95.1s");
        text.Should().NotContain("(no details available)");
    }

    [Fact]
    public void BuildToolDetailLines_EmptyRunningTag_StillFallsBackToNoDetails()
    {
        // 兜底文案保留：三无行（无 Args/Result/LiveProgress）仍显示占位提示，
        // 防止渲染层为空时静默产出零行。
        var tag = new ToolLineTag("Agent", null, null, IsExpanded: true);

        var text = string.Join("\n", MessageRenderer.BuildToolDetailLines(tag, 80).Select(line => line.Text));

        text.Should().Contain("(no details available)");
    }
}
