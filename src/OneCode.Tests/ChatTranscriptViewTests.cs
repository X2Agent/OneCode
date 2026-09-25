using NSubstitute;
using OneCode.App.Tui;
using Terminal.Gui.App;

namespace OneCode.Tests;

/// <summary>
/// <see cref="ChatTranscriptView"/> 的三组渲染契约：
/// 流式期间工具行/思考行/权限提示必须收敛为单行且不被后续增量清除；
/// 已提交内容整体重渲必须保留块顺序、展开状态、尾部交互区域与滚动位置；
/// 模式横幅只保留最新一条，且位置随提交状态正确迁移。
/// </summary>
public sealed class ChatTranscriptViewTests
{
    // 流式渲染

    [Fact]
    public void ToolDone_DuringStreaming_KeepsFollowingTextInSameAssistantBlock()
    {
        var view = new ChatTranscriptView(CreateImmediateApp());

        view.BeginStreaming();
        view.AppendStreamingToken("I will check the tests.");
        view.AddToolStart("Write", null, "call_write");
        view.AddToolDone("Write", isError: true, toolInput: null, result: "Tool 'Write' denied by user.", toolId: "call_write");
        view.AppendStreamingToken(" I can still explain what to do next.");
        view.EndStreaming();

        var lines = view.MessageView.RenderedLines;

        // No assistant header in new UI design — verify content integrity
        string.Join('\n', lines).Should().Contain("Write");
        string.Join('\n', lines).Should().Contain("Tool 'Write' denied by user.");
        string.Join('\n', lines).Should().Contain("I can still");
        string.Join('\n', lines).Should().Contain("explain what to do next.");
    }

    [Fact]
    public void PermissionNotice_DuringStreaming_DoesNotCreateSecondAssistantHeader()
    {
        var view = new ChatTranscriptView(CreateImmediateApp());

        view.BeginStreaming();
        view.AppendStreamingToken("Let me inspect that.");
        view.AddStreamingNotice("Permission denied: Write (denied by user)");
        view.AppendStreamingToken(" I will continue without that tool.");
        view.EndStreaming();

        var lines = view.MessageView.RenderedLines;

        // No assistant header in new UI design — verify content integrity
        string.Join('\n', lines).Should().Contain("Permission denied: Write (denied by user)");
        string.Join('\n', lines).Should().Contain("I will continue");
        string.Join('\n', lines).Should().Contain("without that tool.");
    }

    [Fact]
    public void ToolDone_WithToolId_UpdatesCorrectStartLine_NoDuplicate()
    {
        // Verifies that ToolId-based matching prevents duplicate tool rows
        // when a ToolDone arrives for a tool that has a matching ToolStart.
        var view = new ChatTranscriptView(CreateImmediateApp());

        view.BeginStreaming();
        view.AppendStreamingToken("Working...");
        view.AddToolStart("Read", "file.txt", toolId: "call_abc");
        view.AddToolDone("Read", isError: false, toolInput: "file.txt",
            result: "file contents", toolId: "call_abc");
        view.EndStreaming();

        var lines = view.MessageView.RenderedLines;
        var toolLineCount = lines.Count(l => l.Contains("Read") && l.Contains("file.txt"));
        // Should be exactly 1 tool line — not 2 (which would indicate a duplicate)
        toolLineCount.Should().Be(1);
    }

    [Fact]
    public void ToolDone_AfterContinueStreaming_DoesNotDuplicateCommittedLine()
    {
        // Simulates the scenario where ContinueStreaming clears the pending
        // dictionary between a ToolStart and its ToolDone. The committed
        // ToolStart line is already in history; a late ToolDone must NOT
        // add a second completed row.
        var view = new ChatTranscriptView(CreateImmediateApp());

        view.BeginStreaming();
        view.AppendStreamingToken("First turn.");
        view.AddToolStart("Bash", "ls", toolId: "call_xyz");
        // ContinueStreaming commits the pending lines (including the Bash start line)
        // and clears _pendingToolLines. This simulates a turn boundary.
        view.ContinueStreaming();
        // Now the ToolDone arrives — the pending dict is empty, but the committed
        // start line exists. We should NOT add a duplicate completed row.
        view.AddToolDone("Bash", isError: false, toolInput: "ls",
            result: "file1\nfile2", toolId: "call_xyz");
        view.EndStreaming();

        var lines = view.MessageView.RenderedLines;
        // Count lines containing "Bash" — should be 1 (the committed line),
        // not 2 (committed start + duplicate done).
        var bashLineCount = lines.Count(l => l.Contains("Bash"));
        bashLineCount.Should().Be(1, "the committed ToolStart line should be the only Bash line; a duplicate ToolDone row must not be added after ContinueStreaming cleared the pending state");
    }

    [Fact]
    public void ToolDone_DuplicateEvent_DoesNotCreateSecondRow()
    {
        // Verifies that a duplicate ToolDone event (same ToolId) does not
        // create a second completed row.
        var view = new ChatTranscriptView(CreateImmediateApp());

        view.BeginStreaming();
        view.AddToolStart("Grep", "pattern", toolId: "call_dup");
        view.AddToolDone("Grep", isError: false, toolInput: "pattern",
            result: "match1", toolId: "call_dup");
        // Duplicate ToolDone with the same ToolId — should be ignored, not added
        view.AddToolDone("Grep", isError: false, toolInput: "pattern",
            result: "match1", toolId: "call_dup");
        view.EndStreaming();

        var lines = view.MessageView.RenderedLines;
        var grepLineCount = lines.Count(l => l.Contains("Grep"));
        grepLineCount.Should().Be(1, "duplicate ToolDone events with the same ToolId must not create additional rows");
    }

    [Fact]
    public void ThinkingThenTool_ToolLineSurvivesNextThinkingDelta()
    {
        var view = new ChatTranscriptView(CreateImmediateApp());

        view.BeginStreaming();
        view.AddThinking("Planning the write.");
        view.AddToolStart("Write", "One.txt", toolId: "call_write");
        view.AddToolDone("Write", isError: false, toolInput: "One.txt",
            result: "Wrote One.txt", toolId: "call_write");
        // More thinking after the tool must not wipe the tool row.
        view.AddThinking(" Confirming the file was written.");
        view.AppendStreamingToken("Done.");
        view.EndStreaming();

        var text = string.Join('\n', view.MessageView.RenderedLines);
        text.Should().Contain("Write");
        text.Should().Contain("One.txt");
        text.Should().Contain("Done.");
        CountOccurrences(text, "Thought for").Should().Be(1);
        text.Should().NotContain(" Thinking");
    }

    [Fact]
    public void ThinkingThenTool_ToolLineSurvivesFirstTextToken()
    {
        var view = new ChatTranscriptView(CreateImmediateApp());

        view.BeginStreaming();
        view.AddThinking("I will call Write.");
        view.AddToolStart("Write", "a.txt", toolId: "call_a");
        view.AddToolDone("Write", isError: false, toolInput: "a.txt",
            result: "ok", toolId: "call_a");
        // First reply token collapses thinking; tools after the thinking span must remain.
        view.AppendStreamingToken("File written.");
        view.EndStreaming();

        var text = string.Join('\n', view.MessageView.RenderedLines);
        text.Should().Contain("Write");
        text.Should().Contain("a.txt");
        text.Should().Contain("File written.");
        CountOccurrences(text, "Thought for").Should().Be(1);
    }

    [Fact]
    public void MultiTurnThinking_EachTurnHasIndependentSummary()
    {
        var view = new ChatTranscriptView(CreateImmediateApp());

        view.BeginStreaming();
        view.AddThinking("Phase one plan.");
        view.AddToolStart("Write", "t.txt", toolId: "call_t");
        view.AddToolDone("Write", isError: false, toolInput: "t.txt",
            result: "ok", toolId: "call_t");
        // Agent turn boundary — each turn's thinking is committed independently.
        view.ContinueStreaming();
        view.AddThinking(" Phase two notes.");
        view.AppendStreamingToken("All done.");
        view.EndStreaming();

        var text = string.Join('\n', view.MessageView.RenderedLines);
        text.Should().Contain("Write");
        text.Should().Contain("All done.");
        // Each turn's thinking produces its own "Thought for" summary.
        CountOccurrences(text, "Thought for").Should().Be(2,
            "each turn's thinking should have its own independent summary");
        // Streaming expanded title must not remain in committed history.
        text.Should().NotContain(" Thinking");
    }

    [Fact]
    public void StreamingActivity_TransitionsThroughProcessingThinkingAndReplying()
    {
        var view = new ChatTranscriptView(CreateImmediateApp());
        var activities = new List<string>();
        view.ActivityChanged += activities.Add;

        view.BeginStreaming();
        view.AddThinking("Inspecting the request.");
        view.AppendStreamingToken("Done.");
        view.EndStreaming();

        activities.Should().Equal("处理中", "思考中", "回复中");
    }

    [Fact]
    public void ExpandedToolDetails_SurviveNextThinkingDelta()
    {
        var view = new ChatTranscriptView(CreateImmediateApp());

        view.BeginStreaming();
        view.AddToolStart("Read", "notes.txt", toolId: "call_read");
        view.AddToolDone("Read", isError: false, toolInput: "notes.txt",
            result: "line-one-of-tool-result", toolId: "call_read");

        var toolIdx = -1;
        var rendered = view.MessageView.RenderedLines;
        for (var i = 0; i < rendered.Count; i++)
        {
            if (rendered[i].Contains("Read", StringComparison.Ordinal))
            {
                toolIdx = i;
                break;
            }
        }
        toolIdx.Should().BeGreaterThanOrEqualTo(0);
        view.MessageView.TryToggleExpansionAt(toolIdx).Should().BeTrue();
        string.Join('\n', view.MessageView.RenderedLines).Should().Contain("line-one-of-tool-result");

        view.AddThinking("The user wants a product-research system. The");

        var afterThinking = string.Join('\n', view.MessageView.RenderedLines);
        afterThinking.Should().Contain("line-one-of-tool-result");
        afterThinking.Should().Contain("Read");
        CountOccurrences(afterThinking, " Thinking").Should().Be(1);
    }

    [Fact]
    public void ThinkingUpdates_KeepSingleHeader()
    {
        var view = new ChatTranscriptView(CreateImmediateApp());

        view.BeginStreaming();
        view.AddThinking("The user wants to develop a full-chain system. The");
        view.AddThinking(" next sentence continues the same thought.");
        view.AddThinking(" Still the same turn.");

        var text = string.Join('\n', view.MessageView.RenderedLines);
        CountOccurrences(text, " Thinking").Should().Be(1);
        text.Should().Contain("full-chain system");
        text.Should().Contain("same thought");
    }

    // 已提交内容整体重渲（RerenderCommittedContent）：
    // Plan 侧边栏拖拽/开关与终端 resize 后对话列按新宽度重换行——旧行不再
    // 右侧留白或被截断。重渲必须保留：块顺序与间距、工具/思考展开状态、
    // 尾部交互区域（内联选择器）、流式预览窗口与滚动位置。

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

    // 模式横幅

    /// <summary>Rapid Tab must keep the chat mode banner aligned with the live title-bar mode.</summary>
    [Fact]
    public void UpdateModeBanner_RapidCycle_ReplacesTrailingBanner()
    {
        var view = new ChatTranscriptView(CreateImmediateApp());

        view.UpdateModeBanner(ChatBlockRenderers.RenderModeBanner(WorkingMode.Plan));
        view.UpdateModeBanner(ChatBlockRenderers.RenderModeBanner(WorkingMode.Team));
        view.UpdateModeBanner(ChatBlockRenderers.RenderModeBanner(WorkingMode.Goal));

        var text = string.Join('\n', view.MessageView.RenderedLines);
        text.Should().Contain("GOAL");
        text.Should().NotContain("PLAN");
        text.Should().NotContain("TEAM");
        // Banner is blank line + content line — only one such pair after rapid cycle.
        view.MessageView.RenderedLines.Count(l => l.Contains("GOAL")).Should().Be(1);
    }

    [Fact]
    public void UpdateModeBanner_AfterUserMessage_AppendsNewBanner()
    {
        var view = new ChatTranscriptView(CreateImmediateApp());

        view.UpdateModeBanner(ChatBlockRenderers.RenderModeBanner(WorkingMode.Plan));
        view.AddUserMessageDirect("hello");
        view.UpdateModeBanner(ChatBlockRenderers.RenderModeBanner(WorkingMode.Team));

        var text = string.Join('\n', view.MessageView.RenderedLines);
        text.Should().Contain("PLAN");
        text.Should().Contain("TEAM");
        text.Should().Contain("hello");
    }

    [Fact]
    public void UpdateModeBanner_DuringStreaming_ReplacesBannerAbovePreview()
    {
        var view = new ChatTranscriptView(CreateImmediateApp());

        view.BeginStreaming();
        view.AppendStreamingToken("streaming…");
        view.UpdateModeBanner(ChatBlockRenderers.RenderModeBanner(WorkingMode.Plan));
        view.UpdateModeBanner(ChatBlockRenderers.RenderModeBanner(WorkingMode.Team));
        view.EndStreaming();

        var text = string.Join('\n', view.MessageView.RenderedLines);
        text.Should().Contain("TEAM");
        text.Should().NotContain("PLAN");
        text.Should().Contain("streaming");
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        for (var i = 0; (i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0; i += needle.Length)
            count++;
        return count;
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

    private static IApplication CreateImmediateApp()
    {
        var app = Substitute.For<IApplication>();
        app.Invoke(Arg.Do<Action>(action => action()));
        app.AddTimeout(Arg.Any<TimeSpan>(), Arg.Any<Func<bool>>()).Returns(true);
        return app;
    }
}
