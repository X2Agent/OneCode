using OneCode.App.Services.PlanMode;
using OneCode.App.Query;
using OneCode.Tui;

namespace OneCode.Tests;

public sealed class ChatBlockRenderersTests
{
    [Fact]
    public void RenderModeBanner_Build_ProducesTwoLines()
    {
        var lines = ChatBlockRenderers.RenderModeBanner(WorkingMode.Build);
        lines.Should().HaveCount(2);
        lines[0].FullText.Should().BeEmpty();
        lines[1].FullText.Should().Contain("BUILD");
    }

    [Fact]
    public void RenderModeBanner_Plan_MentionsPlanText()
    {
        var lines = ChatBlockRenderers.RenderModeBanner(WorkingMode.Plan);
        lines[1].FullText.Should().Contain("PLAN");
        lines[1].FullText.Should().Contain("先出计划");
    }

    [Fact]
    public void RenderBuildRunPanel_Planned_HidesInternalState()
    {
        var state = new BuildRunStateEvent(
            new OneCode.Core.Build.BuildRunId("br-1"),
            OneCode.Core.Build.BuildRunState.Planned,
            4,
            0,
            1,
            TerminalReason: null);

        var lines = ChatBlockRenderers.RenderBuildRunPanel(state);
        var text = string.Join("\n", lines.Select(line => line.FullText));

        lines.Should().ContainSingle();
        text.Should().Contain("正在准备执行");
        text.Should().NotContain("br-1");
        text.Contains("Planned", StringComparison.OrdinalIgnoreCase).Should().BeFalse();
    }

    [Theory]
    [InlineData(OneCode.Core.Build.BuildRunState.Blocked, "任务被阻塞")]
    [InlineData(OneCode.Core.Build.BuildRunState.Failed, "任务执行失败")]
    [InlineData(OneCode.Core.Build.BuildRunState.Cancelled, "任务已取消")]
    [InlineData(OneCode.Core.Build.BuildRunState.LimitReached, "达到轮次上限")]
    [InlineData(OneCode.Core.Build.BuildRunState.BudgetExceeded, "达到预算上限")]
    public void RenderBuildRunPanel_TerminalStates_HaveUserFacingMessages(
        OneCode.Core.Build.BuildRunState state,
        string expectedLabel)
    {
        var lines = ChatBlockRenderers.RenderBuildRunPanel(new BuildRunStateEvent(
            new OneCode.Core.Build.BuildRunId("br-1"),
            state,
            5,
            1,
            1,
            OneCode.Core.Workflows.RunTerminalReason.AgentException,
            "failure"));

        lines[0].FullText.Should().Contain(expectedLabel);
        if (state is OneCode.Core.Build.BuildRunState.Blocked or OneCode.Core.Build.BuildRunState.Failed)
            lines.Should().Contain(line => line.FullText.Contains("failure"));
    }

    [Fact]
    public void RenderBuildRunPanel_MultiTask_ShowsActiveAndBlockedCounts()
    {
        var state = new BuildRunStateEvent(
            new OneCode.Core.Build.BuildRunId("br-dag"),
            OneCode.Core.Build.BuildRunState.Implementing,
            8,
            CompletedTasks: 2,
            TotalTasks: 5,
            ActiveTasks: 1,
            BlockedTasks: 2);

        var lines = ChatBlockRenderers.RenderBuildRunPanel(state);

        lines.Should().ContainSingle();
        lines[0].FullText.Should().Contain("正在执行任务（2/5）");
    }

    [Fact]
    public void RenderBuildRunPanel_ReplaySameSnapshot_IsDeterministic()
    {
        var state = new BuildRunStateEvent(
            new OneCode.Core.Build.BuildRunId("br-replay"),
            OneCode.Core.Build.BuildRunState.Verifying,
            8,
            2,
            3);

        var first = ChatBlockRenderers.RenderBuildRunPanel(state).Select(line => line.FullText).ToArray();
        var replay = ChatBlockRenderers.RenderBuildRunPanel(state).Select(line => line.FullText).ToArray();

        replay.Should().Equal(first);
    }

    [Fact]
    public void RenderBuildRunPanel_ConfirmedScope_DoesNotDumpScopeCard()
    {
        var scope = new OneCode.Core.Build.BuildScopeSnapshot(
            "实现 Build M5 阶段化界面",
            ["阶段状态", "交付卡"],
            ["不重构执行引擎"],
            ["保持兼容"],
            [new OneCode.Core.Build.AcceptanceCriterion(
                "a1",
                "窄终端仍可读",
                true)],
            "user",
            DateTimeOffset.Parse("2026-07-31T10:00:00+00:00", CultureInfo.InvariantCulture));
        var state = new BuildRunStateEvent(
            new OneCode.Core.Build.BuildRunId("br-scope"),
            OneCode.Core.Build.BuildRunState.Planned,
            5,
            Scope: scope);

        var lines = ChatBlockRenderers.RenderBuildRunPanel(state);
        var text = string.Join("\n", lines.Select(line => line.FullText));

        lines.Should().ContainSingle();
        text.Should().Contain("正在准备执行");
        text.Should().NotContain("SCOPE CONFIRMATION");
        text.Should().NotContain("实现 Build M5 阶段化界面");
    }

    [Theory]
    [InlineData(40)]
    [InlineData(50)]
    public void RenderBuildRunPanel_NarrowTerminal_PreservesCriticalLabelsWithinWidth(int width)
    {
        var scope = new OneCode.Core.Build.BuildScopeSnapshot(
            "A deliberately long goal that must wrap without removing critical field labels",
            ["A long in-scope item that must remain readable on a narrow terminal"],
            [],
            [],
            [],
            "user",
            DateTimeOffset.Parse("2026-07-31T10:00:00+00:00", CultureInfo.InvariantCulture));
        var state = new BuildRunStateEvent(
            new OneCode.Core.Build.BuildRunId("br-narrow"),
            OneCode.Core.Build.BuildRunState.Verifying,
            12,
            2,
            3,
            Scope: scope,
            ValidationStatus: OneCode.Core.Build.BuildValidationStatus.Pending,
            ChangedFiles: 4,
            TurnsCompleted: 9
            );
        var lines = ChatBlockRenderers.RenderBuildRunPanel(state, width);

        lines.Should().ContainSingle();
        lines.Should().OnlyContain(line => TextWidthHelper.GetDisplayWidth(line.FullText) <= width);
        lines[0].FullText.Should().Contain("正在运行验证");
        lines[0].FullText.Should().NotContain("br-narrow");
        lines[0].FullText.Should().NotContain("seq");
    }

    [Fact]
    public void RenderBuildDeliveryCard_ShowsValidationAndTransaction()
    {
        var result = new OneCode.Core.Build.BuildRunResult(
            new OneCode.Core.Build.BuildRunId("br-delivery"),
            OneCode.Core.Build.BuildRunState.Completed,
            OneCode.Core.Workflows.RunTerminalReason.Completed,
            "done",
            ["Foo.cs"],
            [],
            [new OneCode.Core.Build.BuildValidationRun(
                "v1",
                OneCode.Core.Build.BuildValidationStatus.Passed,
                [],
                ["passed"],
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow)],
            [],
            [],
            null,
            true,
            false,
            OneCode.Core.Build.BuildRunMetrics.Empty);

        var lines = ChatBlockRenderers.RenderBuildDeliveryCard(result);

        lines.Should().Contain(line => line.FullText.Contains("BUILD DELIVERY"));
        lines.Should().Contain(line => line.FullText.Contains("Validation  Passed"));
        lines.Should().Contain(line => line.FullText.Contains("Acceptance  0/0"));
        lines.Should().Contain(line => line.FullText.Contains("Incomplete  0"));
        lines.Should().Contain(line => line.FullText.Contains("Transaction committed"));
    }

    [Fact]
    public void RenderBuildDeliveryCard_OmitsSummaryFieldToAvoidDuplicatingAssistantMessage()
    {
        var longSummary = "已完成，交付文件：**`E:\\OneCode\\.dev-workspace\\sun-yuchen-timeline.html`**（46KB 单文件）";
        var result = new OneCode.Core.Build.BuildRunResult(
            new OneCode.Core.Build.BuildRunId("br-summary"),
            OneCode.Core.Build.BuildRunState.Completed,
            OneCode.Core.Workflows.RunTerminalReason.Completed,
            longSummary,
            ["sun-yuchen-timeline.html"],
            [],
            [],
            [],
            [],
            null,
            true,
            false,
            OneCode.Core.Build.BuildRunMetrics.Empty);

        var lines = ChatBlockRenderers.RenderBuildDeliveryCard(result, viewWidth: 50);

        lines.Should().Contain(line => line.FullText.Contains("Files       1"));
        lines.Should().NotContain(line => line.FullText.Contains("Summary"),
            "the assistant's final markdown summary is already rendered above the delivery card");
        lines.Should().NotContain(line => line.FullText.Contains("sun-yuchen-timeline.html"),
            "raw markdown summary text must not be duplicated or truncated inside the receipt card");
    }

    [Fact]
    public void InlineSelector_UsesSharedInformationRequestCardHeader()
    {
        var lines = InlineSelector.RenderAsLines(
            "向用户提问",
            [new InlineSelectorOption("a", "方案 A")],
            selectedIndex: 0,
            prompt: "选择实现方案？",
            useInformationRequestCard: true);
        var text = string.Join("\n", lines.Select(line => line.FullText));

        text.Should().Contain("需要补充信息");
        text.Should().Contain("向用户提问");
        text.Should().Contain("选择实现方案？");
        text.Should().Contain("方案 A");
    }

    [Fact]
    public void InlineSelector_StandardApproval_DoesNotUseInformationRequestHeader()
    {
        var lines = InlineSelector.RenderAsLines(
            "请审批以上计划",
            [new InlineSelectorOption("approve", "批准计划")],
            selectedIndex: 0);
        var text = string.Join("\n", lines.Select(line => line.FullText));

        text.Should().Contain("请审批以上计划");
        text.Should().NotContain("需要补充信息");
    }

    [Fact]
    public void RenderDiffBlock_HeaderMentionsFileName()
    {
        var lines = ChatBlockRenderers.RenderDiffBlock("Foo.cs", new[] { "+ x" }, new[] { "- y" }, 1, 1);
        lines[0].FullText.Should().Contain("Foo.cs");
        lines[0].FullText.Should().Contain("+1");
        lines[0].FullText.Should().Contain("-1");
    }

    [Fact]
    public void RenderDiffBlock_EmitsAddedAndRemovedLines()
    {
        var lines = ChatBlockRenderers.RenderDiffBlock(
            "F.cs",
            new[] { "new line" },
            new[] { "old line" });
        // header + 1 added + 1 removed = 3
        lines.Should().HaveCount(3);
        lines[1].FullText.Should().Contain("+");
        lines[1].FullText.Should().Contain("new line");
        lines[2].FullText.Should().Contain("-");
        lines[2].FullText.Should().Contain("old line");
    }

    [Fact]
    public void RenderDiffBlock_WithAgentName_ShowsAttributionInHeader()
    {
        var withAgent = ChatBlockRenderers.RenderDiffBlock(
            "A.cs", new[] { "+ x" }, [], agentName: "executor-1");
        withAgent[0].FullText.Should().Contain("A.cs");
        withAgent.Should().Contain(l => l.FullText.Contains("by executor-1"));

        // 无归属（主 agent 路径）不出现 by 标注。
        var withoutAgent = ChatBlockRenderers.RenderDiffBlock(
            "B.cs", new[] { "+ x" }, []);
        withoutAgent.Should().NotContain(l => l.FullText.Contains("by "));
    }

    [Fact]
    public void RenderPlanCard_DoneStepShowsCheckmarkAndSuccessColor()
    {
        var lines = ChatBlockRenderers.RenderPlanCard("Plan",
            [new PlanStep("X", Status: PlanStepStatus.Done, Content: "description")]);
        var stepLine = lines.First(l => l.FullText.Contains("①"));
        stepLine.FullText.Should().Contain($"{TuiGlyphs.Complete} ①");
        stepLine.Color.Should().Be(TuiPalette.Success,
            "completed steps must be visually distinct from the muted gray step description below");
    }

    [Fact]
    public void RenderPlanCard_IncludeHeaderFalse_OmitsTitleLines()
    {
        // 侧边栏固定标题行（SidebarViewBase）已渲染一次标题；对话流卡片内再渲染一遍
        // 就是标题重复（历史缺陷：标题出现两次）。嵌入侧边栏时传 includeHeader:false。
        var withHeader = ChatBlockRenderers.RenderPlanCard(
            "我的计划", [new PlanStep("步骤一")], includeHeader: true);
        var withoutHeader = ChatBlockRenderers.RenderPlanCard(
            "我的计划", [new PlanStep("步骤一")], includeHeader: false);

        withHeader.Should().Contain(l => l.FullText.Contains("我的计划"));
        withoutHeader.Should().Contain(l => l.FullText.Contains("步骤一"),
            "includeHeader 只隐藏标题，计划内容必须照常渲染");
        withoutHeader.Should().NotContain(l => l.FullText.Contains("我的计划"),
            "嵌入侧边栏时标题只保留固定标题行那一处");
    }

    [Fact]
    public void RenderPlanCard_LongDocumentPath_WrapsAsSingleEntryWithContinuationIndent()
    {
        // 侧栏宽度有限，计划文档路径（>60 列）必然换行。回归守卫：换行续行只缩进，
        // 不重复「计划文档」前缀——重复前缀会让一条路径被误读为多个计划文档。
        var path = @"C:\Users\mayue\.onecode\plans\a0e7142e2f9a41bb8cbe36c62e45e6f\6f9ca8872c1e4b0f\revision-0001.md";
        var lines = ChatBlockRenderers.RenderPlanCard(
            "Plan", [], viewWidth: 30, documentPath: path);

        lines.Count(l => l.FullText.Contains("计划文档")).Should().Be(1,
            "one document path must render as exactly one prefixed entry");
        var entry = lines.First(l => l.FullText.Contains("计划文档"));
        var continuation = lines.SkipWhile(l => !ReferenceEquals(l, entry))
            .Skip(1)
            .TakeWhile(l => l.FullText.StartsWith(' '))
            .ToList();
        continuation.Should().NotBeEmpty("the long path must continue on indented lines");
        continuation.Should().OnlyContain(l => !l.FullText.Contains("计划文档"),
            "continuation lines must be indented, not re-prefixed");
        string.Concat(entry.FullText.Trim() + string.Concat(continuation.Select(l => l.FullText.Trim())))
            .Should().Contain("revision-0001.md", "the full path must survive wrapping");
    }

    [Fact]
    public void RenderPlanCard_CurrentStepHighlighted()
    {
        var lines = ChatBlockRenderers.RenderPlanCard("Plan",
            new[] { new PlanStep("X", Status: PlanStepStatus.Current) });
        var stepLine = lines.First(l => l.FullText.Contains("①"));
        stepLine.FullText.Should().Contain($"{TuiGlyphs.InProgress} ①");
        stepLine.Color.Should().Be(TuiPalette.Accent);
    }

    [Theory]
    [InlineData(WorkingMode.Plan, "正在整理计划", ModeProgressState.Running)]
    [InlineData(WorkingMode.Goal, "目标未完成", ModeProgressState.Failed)]
    public void RenderModeProgress_RendersOneUserFacingLine(
        WorkingMode mode,
        string message,
        ModeProgressState state)
    {
        var lines = ChatBlockRenderers.RenderModeProgress(
            new ModeProgressEvent(mode, message, state, 2, 5),
            viewWidth: 48);

        lines.Should().ContainSingle();
        lines[0].FullText.Should().Contain(message);
        lines[0].FullText.Should().Contain("2/5");
        lines[0].FullText.Should().NotContain("RunId");
        lines[0].FullText.Should().NotContain("Budget");
        TextWidthHelper.GetDisplayWidth(lines[0].FullText).Should().BeLessThanOrEqualTo(48);
    }

    [Fact]
    public void RenderAgentMessage_HasHeaderAndContent()
    {
        var lines = ChatBlockRenderers.RenderAgentMessage("executor", "done");
        lines.Should().Contain(l => l.FullText.Contains("Executor"));
        lines.Should().Contain(l => l.FullText.Contains("done"));
        var headerLine = lines.First(l => l.FullText.Contains("Executor"));
        headerLine.Segments.Should().NotBeNull();
        headerLine.Segments!.Should().Contain(s => s.Text.Contains("\u25b8"));
    }
}