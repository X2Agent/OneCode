using NSubstitute;
using OneCode.App.Tui;
using OneCode.Core.Domain;
using OneCode.Core.Tools;
using Terminal.Gui.App;

namespace OneCode.Tests;

/// <summary>
/// 工具行同轮同类合并（防刷屏）的渲染契约：
/// 流内相邻同名完成行合并为 ×N 组行（展开逐条列出目标/摘要/时长），异名工具断组；
/// 历史回放（LoadConversation）按段聚合出同样的组行；单条调用展开仍是完整详情。
/// HTML 剥离（HtmlPlainText）保证搜索摘要无标签噪音。
/// </summary>
public sealed class ToolRowGroupingTests
{
    // —— 流内合并 ——

    [Fact]
    public void ConsecutiveSameToolDones_MergeIntoSingleGroupRow()
    {
        var view = new ChatTranscriptView(CreateImmediateApp());

        view.BeginStreaming();
        for (var i = 0; i < 3; i++)
        {
            view.AddToolStart("WebSearch", $"{{\"query\":\"q{i}\"}}", toolId: $"call_{i}");
            view.AddToolDone("WebSearch", isError: false, toolInput: $"{{\"query\":\"q{i}\"}}",
                result: $"{{\"provider\":\"tavily\",\"query\":\"q{i}\",\"results\":[{{}}]}}", toolId: $"call_{i}");
        }
        view.EndStreaming();

        var toolLines = view.MessageView.RenderedLines.Where(l => l.Contains("WebSearch")).ToList();
        toolLines.Should().ContainSingle("三次同类调用合并为一行组行");
        toolLines.Single().Should().Contain("×3");
    }

    [Fact]
    public void DifferentToolName_BreaksTheGroup()
    {
        var view = new ChatTranscriptView(CreateImmediateApp());

        view.BeginStreaming();
        for (var i = 0; i < 2; i++)
        {
            view.AddToolStart("WebSearch", $"{{\"query\":\"q{i}\"}}", toolId: $"ws_{i}");
            view.AddToolDone("WebSearch", isError: false, toolInput: $"{{\"query\":\"q{i}\"}}",
                result: "{{\"results\":[]}}", toolId: $"ws_{i}");
        }
        view.AddToolStart("ToolSearch", "cron", toolId: "ts_0");
        view.AddToolDone("ToolSearch", isError: false, toolInput: "cron", result: "[]", toolId: "ts_0");
        view.AddToolStart("WebSearch", "{\"query\":\"q9\"}", toolId: "ws_9");
        view.AddToolDone("WebSearch", isError: false, toolInput: "{\"query\":\"q9\"}",
            result: "{\"results\":[]}", toolId: "ws_9");
        view.EndStreaming();

        var toolLines = view.MessageView.RenderedLines.Where(l => l.Contains("WebSearch") || l.Contains("ToolSearch")).ToList();

        toolLines.Should().HaveCount(3, "异名工具打断合并：WebSearch ×2 + ToolSearch + WebSearch ×1");
        toolLines.Count(l => l.Contains("×2")).Should().Be(1);
        toolLines.Count(l => l.Contains("×3")).Should().Be(0, "断组后不得跨 ToolSearch 累计");
    }

    [Fact]
    public void GroupRow_Expanded_ListsEachQueryWithDuration()
    {
        var view = new ChatTranscriptView(CreateImmediateApp());

        view.BeginStreaming();
        for (var i = 0; i < 2; i++)
        {
            view.AddToolStart("WebSearch", $"{{\"query\":\"query-{i}\"}}", toolId: $"call_{i}");
            view.AddToolDone("WebSearch", isError: false, toolInput: $"{{\"query\":\"query-{i}\"}}",
                result: "{\"results\":[]}", toolId: $"call_{i}");
        }
        view.EndStreaming();

        var groupLineIndex = view.MessageView.RenderedLines
            .Select((text, index) => (text, index))
            .First(entry => entry.text.Contains("×2")).index;

        view.MessageView.TryToggleExpansionAt(groupLineIndex).Should().BeTrue();

        var expanded = string.Join('\n', view.MessageView.RenderedLines);
        expanded.Should().Contain("query-0");
        expanded.Should().Contain("query-1");
    }

    // —— 历史回放聚合 ——

    [Fact]
    public void LoadConversation_MergesConsecutiveSameToolPairsIntoGroupRow()
    {
        var view = new ChatTranscriptView(CreateImmediateApp());
        var conversation = CreateConversationWithToolPairs(("WebSearch", "q0"), ("WebSearch", "q1"), ("WebSearch", "q2"));

        view.LoadConversation(conversation);

        var toolLines = view.MessageView.RenderedLines.Where(l => l.Contains("WebSearch")).ToList();
        toolLines.Should().ContainSingle("历史回放同样聚合为一行组行");
        toolLines.Single().Should().Contain("×3");
    }

    [Fact]
    public void LoadConversation_DifferentToolNameBreaksRun()
    {
        var view = new ChatTranscriptView(CreateImmediateApp());
        var conversation = CreateConversationWithToolPairs(("WebSearch", "q0"), ("Read", "notes.txt"), ("WebSearch", "q1"));

        view.LoadConversation(conversation);

        var rendered = string.Join('\n', view.MessageView.RenderedLines);
        rendered.Should().Contain("notes.txt", "单条 Read 调用显示目标路径");
        var webSearchLines = view.MessageView.RenderedLines.Where(l => l.Contains("WebSearch")).ToList();
        webSearchLines.Should().HaveCount(2, "Read 打断后两次 WebSearch 各自成单行");
    }

    [Fact]
    public void SegmentConversation_BoundaryAtTextAssistant()
    {
        var (user, toolAssistant, result, textAssistant) = BuildMessages("WebSearch", "q0");
        var conversation = CreateConversation();
        conversation.Messages.Add(user);
        conversation.Messages.Add(toolAssistant);
        conversation.Messages.Add(result);
        conversation.Messages.Add(textAssistant);

        var units = MessageFlowRenderer.SegmentConversation(conversation.Messages);

        units.Should().HaveCount(3, "user / 工具段 / 纯文本 assistant");
        units[1].Should().BeOfType<MessageFlowRenderer.ToolRunSegment>();
    }

    [Fact]
    public void RenderToolRunSegment_SinglePair_RendersCompletedLineWithTarget()
    {
        var (_, toolAssistant, result, _) = BuildMessages("WebSearch", "公司财报");
        var renderer = new MessageFlowRenderer { CurrentWidth = 120 };

        var lines = renderer.RenderToolRunSegment(new MessageFlowRenderer.ToolRunSegment
        {
            Assistants = [toolAssistant],
            Results = [result],
        });

        var text = string.Join('\n', lines.Select(l => l.FullText));
        text.Should().Contain("WebSearch");
        text.Should().Contain("公司财报", "历史单条调用显示搜索词目标");
        text.Should().NotContain("×", "单条不显示组计数");
    }

    // —— HTML 剥离 ——

    [Fact]
    public void HtmlPlainText_StripsTagsAndDecodesEntities()
    {
        var stripped = HtmlPlainText.Strip("<p style=\"margin-top:10px\">生成时间：2026-10-02</p> <footer>&amp;copy; Example</footer>");

        stripped.Should().Be("生成时间：2026-10-02 &copy; Example");
        stripped.Should().NotContain("<");
    }

    [Fact]
    public void HtmlPlainText_EmptyInput_ReturnsEmpty()
    {
        HtmlPlainText.Strip(null).Should().BeEmpty();
        HtmlPlainText.Strip("   ").Should().BeEmpty();
    }

    // —— 构造辅助 ——

    private static Conversation CreateConversation()
        => new() { Name = "t", CreatedAt = DateTimeOffset.UtcNow };

    private static Conversation CreateConversationWithToolPairs(params (string Tool, string Query)[] pairs)
    {
        var conversation = CreateConversation();
        conversation.Messages.Add(BuildMessages("x", "seed").User);
        foreach (var (tool, query) in pairs)
        {
            var (_, toolAssistant, result, _) = BuildMessages(tool, query);
            conversation.Messages.Add(toolAssistant);
            conversation.Messages.Add(result);
        }

        return conversation;
    }

    private static (UserMessage User, AssistantMessage ToolAssistant, ToolResultMessage Result, AssistantMessage TextAssistant)
        BuildMessages(string tool, string query)
    {
        var user = new UserMessage(Guid.NewGuid().ToString("N"), "调研", DateTimeOffset.UtcNow);
        var toolAssistant = new AssistantMessage(
            Guid.NewGuid().ToString("N"),
            Content: [new ToolUseBlock($"call_{query}", tool, $"{{\"query\":\"{query}\"}}")],
            Timestamp: DateTimeOffset.UtcNow);
        var result = new ToolResultMessage(
            Guid.NewGuid().ToString("N"),
            ToolUseId: $"call_{query}",
            ToolName: tool,
            Content: $"{{\"provider\":\"tavily\",\"query\":\"{query}\",\"results\":[{{\"title\":\"t\",\"url\":\"https://example.com\",\"snippet\":\"s\"}}]}}",
            IsError: false,
            Timestamp: DateTimeOffset.UtcNow);
        var textAssistant = new AssistantMessage(
            Guid.NewGuid().ToString("N"),
            Content: [new TextBlock("调研结论")],
            Timestamp: DateTimeOffset.UtcNow);
        return (user, toolAssistant, result, textAssistant);
    }

    // —— 孤儿结果兜底 ——

    [Fact]
    public void ToolDone_UnknownToolId_RendersResultInsteadOfDropping()
    {
        // 反证：未知 ToolId（未见起始行）的完成结果曾两条分支皆不命中而整条丢失。
        var view = new ChatTranscriptView(CreateImmediateApp());

        view.BeginStreaming();
        view.AddToolDone("WebSearch", isError: false, toolInput: "{\"query\":\"q\"}",
            result: "{\"hits\":42}", toolId: "call_never_started");
        view.EndStreaming();

        view.MessageView.RenderedLines
            .Where(l => l.Contains("WebSearch"))
            .Should().NotBeEmpty("孤儿结果必须渲染为独立完成行（含结果摘要），不得静默丢弃");
    }

    private static IApplication CreateImmediateApp()
    {
        var app = Substitute.For<IApplication>();
        app.Invoke(Arg.Do<Action>(action => action()));
        app.AddTimeout(Arg.Any<TimeSpan>(), Arg.Any<Func<bool>>()).Returns(true);
        return app;
    }
}
