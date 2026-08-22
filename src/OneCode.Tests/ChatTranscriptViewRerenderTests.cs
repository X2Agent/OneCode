using NSubstitute;
using OneCode.App.Tui;
using Terminal.Gui.App;

namespace OneCode.Tests;

/// <summary>
/// 已提交内容整体重渲（<see cref="ChatTranscriptView.RerenderCommittedContent"/>）：
/// Plan 侧边栏拖拽/开关与终端 resize 后对话列按新宽度重换行——旧行不再
/// 右侧留白或被截断。重渲必须保留：块顺序与间距、工具/思考展开状态、
/// 尾部交互区域（内联选择器）、流式预览窗口与滚动位置。
/// </summary>
public sealed class ChatTranscriptViewRerenderTests
{
    [Fact]
    public void Rerender_UserMessage_RewrapsToNarrowerWidth()
    {
        var view = new ChatTranscriptView(CreateImmediateApp());
        // 视口未布局时 ContentWidth 回退为 80：长文本先按 80 列换行。
        view.AddUserMessageDirect(new string('a', 200));

        var before = view.MessageView.RenderedLines;
        before.Should().NotBeEmpty();
        before.Count(l => l.Length > 0).Should().BeGreaterThan(1);

        view.RerenderCommittedContent(40);

        var after = view.MessageView.RenderedLines;
        after.Should().NotBeEmpty();
        // 全部行按新宽度换行：没有任何行超过 40 列。
        after.Where(l => l.Length > 0).All(l => l.Length <= 40).Should().BeTrue();
        // 文本内容完整保留（时间戳在首行的落点随宽度移动，仅比对正文字符）。
        string.Concat(after).Count(c => c == 'a').Should().Be(200);
    }

    [Fact]
    public void Rerender_MultipleBlocks_PreservesOrderSpacingAndContent()
    {
        var view = new ChatTranscriptView(CreateImmediateApp());
        view.AddUserMessageDirect("user message body");
        view.AddSystem("system notice");
        view.AddCommandResult("command result body");

        var before = view.MessageView.RenderedLines;

        view.RerenderCommittedContent(100);

        var after = view.MessageView.RenderedLines;
        // 行数与块顺序不变；每块的内容逐块对应（填充空格随宽度变化，忽略之）。
        after.Should().HaveCount(before.Count);
        for (var i = 0; i < before.Count; i++)
            after[i].Replace(" ", "", StringComparison.Ordinal)
                .Should().Be(before[i].Replace(" ", "", StringComparison.Ordinal));
    }

    [Fact]
    public void Rerender_ToolExpansionState_PreservedAndReflowed()
    {
        var view = new ChatTranscriptView(CreateImmediateApp());
        var result = string.Join('\n', Enumerable.Repeat("line-one-of-tool-result", 8));
        view.AddToolDone("Read", isError: false, toolInput: null, result, toolId: "t1");

        // 展开工具行
        var toolIdx = IndexOf(view.MessageView.RenderedLines, l => l.Contains("Read", StringComparison.Ordinal));
        toolIdx.Should().BeGreaterThanOrEqualTo(0);
        view.MessageView.TryToggleExpansionAt(toolIdx).Should().BeTrue();
        string.Join('\n', view.MessageView.RenderedLines)
            .Should().Contain("line-one-of-tool-result");

        view.RerenderCommittedContent(100);

        var text = string.Join('\n', view.MessageView.RenderedLines);
        // 展开状态跨重渲保留，细节行按新宽度重建。
        text.Should().Contain("line-one-of-tool-result");
        view.MessageView.RenderedLines
            .Count(l => l.Contains("Read", StringComparison.Ordinal))
            .Should().Be(1);
    }

    [Fact]
    public void Rerender_TailRegion_PreservedAtEnd()
    {
        var view = new ChatTranscriptView(CreateImmediateApp());
        view.AddUserMessageDirect("before selector");

        var selectorLines = new[]
        {
            FormattedLine.Plain("请审批右侧计划", TuiPalette.FgPrimary),
            FormattedLine.Plain("▸ 批准并执行", TuiPalette.Accent),
        };
        view.MessageView.BeginTailRegion(selectorLines);

        view.RerenderCommittedContent(60);

        var lines = view.MessageView.RenderedLines;
        lines.Should().Contain("请审批右侧计划");
        lines[^2].Should().Be("请审批右侧计划");
        lines[^1].Should().Be("▸ 批准并执行");
    }

    [Fact]
    public void Rerender_StreamingPreview_RebuiltAtNewWidth()
    {
        var view = new ChatTranscriptView(CreateImmediateApp());
        view.AddUserMessageDirect("question");
        view.BeginStreaming();
        view.AppendStreamingToken(new string('b', 200));

        view.RerenderCommittedContent(40);

        var lines = view.MessageView.RenderedLines;
        // 预览窗口按新宽度重建：文本行不超过新宽度，内容完整。
        lines.Should().Contain(l => l.Contains('b'));
        lines.Where(l => l.Contains('b')).All(l => l.Length <= 40).Should().BeTrue();
        // 用户消息（已提交块）仍在预览之前。
        IndexOf(lines, l => l.Contains("question", StringComparison.Ordinal))
            .Should().BeLessThan(IndexOf(lines, l => l.Contains('b')));
    }

    [Fact]
    public void Rerender_CommittedStream_TextReflowsAtNewWidth()
    {
        var view = new ChatTranscriptView(CreateImmediateApp());
        view.AddUserMessageDirect("question");
        view.BeginStreaming();
        view.AppendStreamingToken(new string('c', 120));
        view.EndStreaming();

        var wide = view.MessageView.RenderedLines;

        view.RerenderCommittedContent(30);

        var narrow = view.MessageView.RenderedLines;
        narrow.Count(l => l.Contains('c')).Should().BeGreaterThan(wide.Count(l => l.Contains('c')));
        narrow.Where(l => l.Contains('c')).All(l => l.Length <= 30).Should().BeTrue();
        Normalize(wide).Should().Contain(new string('c', 120));
    }

    [Fact]
    public void Rerender_ModeBanner_LatestBannerOnly()
    {
        var view = new ChatTranscriptView(CreateImmediateApp());
        view.AddUserMessageDirect("msg");
        view.UpdateModeBanner(ChatBlockRenderers.RenderModeBanner(WorkingMode.Build));
        view.UpdateModeBanner(ChatBlockRenderers.RenderModeBanner(WorkingMode.Plan));

        view.RerenderCommittedContent(90);

        var text = string.Join('\n', view.MessageView.RenderedLines);
        text.Should().NotContain("BUILD");
        text.Should().Contain("PLAN");
        text.Should().Contain("msg");
    }

    [Fact]
    public void Rerender_ScrollOffset_PreservedWhenNotFollowingBottom()
    {
        var view = new ChatTranscriptView(CreateImmediateApp());
        for (var i = 0; i < 50; i++)
            view.AddUserMessageDirect($"message-{i:00}");
        // 向下滚 3 行使偏移非零，再向上 1 行脱离跟随底部模式。
        view.MessageView.ScrollDown(3);
        view.MessageView.ScrollUp(1);
        var offsetBefore = view.MessageView.ScrollOffset;
        offsetBefore.Should().Be(2);

        view.RerenderCommittedContent(80);

        view.MessageView.ScrollOffset.Should().Be(offsetBefore);
    }

    private static IApplication CreateImmediateApp()
    {
        var app = Substitute.For<IApplication>();
        app.Invoke(Arg.Do<Action>(action => action()));
        app.AddTimeout(Arg.Any<TimeSpan>(), Arg.Any<Func<bool>>()).Returns(true);
        return app;
    }

    private static int IndexOf(IReadOnlyList<string> lines, Func<string, bool> predicate)
    {
        for (var i = 0; i < lines.Count; i++)
        {
            if (predicate(lines[i]))
                return i;
        }
        return -1;
    }

    private static string Normalize(IReadOnlyList<string> lines)
        => string.Concat(lines.Select(l => l.Replace(" ", "", StringComparison.Ordinal)));
}
