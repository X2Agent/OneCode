using OneCode.App.Tui;

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
    public void RenderModeBanner_TeamConfig_MentionsYamlDefault()
    {
        var lines = ChatBlockRenderers.RenderModeBanner(WorkingMode.Team, TeamStrategy.Config);
        lines[1].FullText.Should().Contain("Config");
        lines[1].FullText.Should().Contain("YAML");
    }

    [Fact]
    public void RenderModeBanner_TeamMagentic_MentionsOrchestrator()
    {
        var lines = ChatBlockRenderers.RenderModeBanner(WorkingMode.Team, TeamStrategy.Magentic);
        lines[1].FullText.Should().Contain("Magentic");
        lines[1].FullText.Should().Contain("Orchestrator");
    }

    [Fact]
    public void RenderModeBanner_TeamGroupChat_MentionsRoundRobin()
    {
        var lines = ChatBlockRenderers.RenderModeBanner(WorkingMode.Team, TeamStrategy.GroupChat);
        lines[1].FullText.Should().Contain("GroupChat");
    }

    [Fact]
    public void RenderBuildRunPanel_Clarifying_HidesInternalStateAndQuestions()
    {
        var state = new TuiBuildRunState(
            new OneCode.Core.Build.BuildRunId("br-1"),
            OneCode.Core.Build.BuildRunState.Clarifying,
            4,
            ["明确目标", "确认验收"],
            0,
            1,
            OneCode.Core.Build.BuildTerminalReason.ClarificationRequired);

        var lines = ChatBlockRenderers.RenderBuildRunPanel(state);
        var text = string.Join("\n", lines.Select(line => line.FullText));

        lines.Should().ContainSingle();
        text.Should().Contain("等待补充任务信息");
        text.Should().NotContain("br-1");
        text.Contains("Clarifying", StringComparison.OrdinalIgnoreCase).Should().BeFalse();
        text.Should().NotContain("明确目标");
        text.Should().NotContain("确认验收");
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
        var lines = ChatBlockRenderers.RenderBuildRunPanel(new TuiBuildRunState(
            new OneCode.Core.Build.BuildRunId("br-1"),
            state,
            5,
            [],
            1,
            1,
            OneCode.Core.Build.BuildTerminalReason.AgentException,
            "failure"));

        lines[0].FullText.Should().Contain(expectedLabel);
        if (state is OneCode.Core.Build.BuildRunState.Blocked or OneCode.Core.Build.BuildRunState.Failed)
            lines.Should().Contain(line => line.FullText.Contains("failure"));
    }

    [Fact]
    public void RenderBuildRunPanel_MultiTask_ShowsActiveAndBlockedCounts()
    {
        var state = new TuiBuildRunState(
            new OneCode.Core.Build.BuildRunId("br-dag"),
            OneCode.Core.Build.BuildRunState.Implementing,
            8,
            [],
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
        var state = new TuiBuildRunState(
            new OneCode.Core.Build.BuildRunId("br-replay"),
            OneCode.Core.Build.BuildRunState.Verifying,
            8,
            [],
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
        var state = new TuiBuildRunState(
            new OneCode.Core.Build.BuildRunId("br-scope"),
            OneCode.Core.Build.BuildRunState.Planning,
            5,
            [],
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
        var state = new TuiBuildRunState(
            new OneCode.Core.Build.BuildRunId("br-narrow"),
            OneCode.Core.Build.BuildRunState.Verifying,
            12,
            [],
            2,
            3,
            Scope: scope,
            ValidationStatus: OneCode.Core.Build.BuildValidationStatus.Pending,
            ChangedFiles: 4,
            TurnsCompleted: 9,
            EstimatedCost: 0.42m);

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
            OneCode.Core.Build.BuildTerminalReason.Completed,
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
    public void RenderToolCallRow_Success_ProducesCheckMark()
    {
        var lines = ChatBlockRenderers.RenderToolCallRow("Read", "file.txt", ok: true);
        lines.Should().HaveCount(1);
        lines[0].FullText.Should().Contain("Read");
        lines[0].FullText.Should().Contain("file.txt");
        lines[0].FullText.Should().Contain("完成");
    }

    [Fact]
    public void RenderToolCallRow_Failure_ProducesCrossMark()
    {
        var lines = ChatBlockRenderers.RenderToolCallRow("Bash", null, ok: false);
        lines[0].FullText.Should().Contain("错误");
    }

    [Fact]
    public void RenderToolCallRow_NoArgs_OmitsArgSegment()
    {
        var lines = ChatBlockRenderers.RenderToolCallRow("Read");
        lines[0].FullText.Should().NotContain("null");
    }

    [Fact]
    public void RenderToolCallRow_IsMultiSegment()
    {
        var lines = ChatBlockRenderers.RenderToolCallRow("Read", "file.txt", true);
        lines[0].Segments.Should().NotBeNull();
        lines[0].Segments!.Count.Should().BeGreaterThan(1);
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
    public void RenderPlanCard_TitleAndStepsRendered()
    {
        var steps = new List<PlanStep>
        {
            new("Extract TuiInitializer class", Assignee: "executor", Status: PlanStepStatus.Done),
            new("Inject ITuiInitializer", Assignee: "executor", Status: PlanStepStatus.Current),
            new("Update unit tests", Assignee: "reviewer", Status: PlanStepStatus.Pending),
        };
        // showActionButtons: true 显式启用按钮栏——默认 false（仅 PendingApproval 阶段由
        // InlineSelector 接管决策，按钮栏不再渲染，见 #3 修复）。
        var lines = ChatBlockRenderers.RenderPlanCard("重构计划", steps, showActionButtons: true);
        lines.Should().Contain(l => l.FullText.Contains("重构计划"));
        lines.Last().FullText.Should().Contain("批准");
        lines.Last().FullText.Should().Contain("拒绝");
        lines.Should().Contain(l => l.FullText.Contains("→ executor"));
        lines.Should().Contain(l => l.FullText.Contains("→ reviewer"));
    }

    [Fact]
    public void RenderPlanCard_DefaultHidesActionButtons()
    {
        // 默认不渲染 a/r/s 按钮栏——决策入口改为 InlineSelector。
        // 仅当显式传入 showActionButtons: true 时才渲染。
        var lines = ChatBlockRenderers.RenderPlanCard("Plan",
            new[] { new PlanStep("Step 1") });
        lines.Should().NotContain(l => l.FullText.Contains("批准"));
        lines.Should().NotContain(l => l.FullText.Contains("拒绝"));
    }

    [Fact]
    public void RenderPlanCard_PendingApproval_ShowsFullPlanAndClearGuidance()
    {
        var lines = ChatBlockRenderers.RenderPlanCard(
            "最终计划",
            [new PlanStep("修改渲染链路", "统一处理 Unicode")],
            viewWidth: 60,
            showApprovalGuidance: true,
            markdown: "# 最终计划\n\n## 验证\n\n- 运行测试");
        var text = string.Join("\n", lines.Select(line => line.FullText));

        text.Should().Contain("完整计划");
        text.Should().Contain("验证");
        text.Should().Contain("运行测试");
        text.Should().Contain("批准并执行");
        text.Should().Contain("输入修改意见");
        lines.Should().OnlyContain(line => TextWidthHelper.GetDisplayWidth(line.FullText) <= 60);
    }

    [Fact]
    public void RenderPlanCard_LongStepContent_WrapsToViewWidth()
    {
        var lines = ChatBlockRenderers.RenderPlanCard(
            "Plan",
            [new PlanStep("Step", string.Join("", Enumerable.Repeat("详细说明", 30)))],
            viewWidth: 40);

        lines.Should().OnlyContain(line => TextWidthHelper.GetDisplayWidth(line.FullText) <= 40);
    }

    [Fact]
    public void RenderPlanCard_DoneStepShowsCircledNumber()
    {
        var lines = ChatBlockRenderers.RenderPlanCard("Plan",
            new[] { new PlanStep("X", Status: PlanStepStatus.Done) });
        lines.Should().Contain(l => l.FullText.Contains("①"));
    }

    [Fact]
    public void RenderPlanCard_CurrentStepHighlighted()
    {
        var lines = ChatBlockRenderers.RenderPlanCard("Plan",
            new[] { new PlanStep("X", Status: PlanStepStatus.Current) });
        var stepLine = lines.First(l => l.FullText.Contains("①"));
        stepLine.Color.Should().Be(TuiPalette.Accent);
    }

    [Theory]
    [InlineData(WorkingMode.Plan, "正在整理计划", ModeProgressState.Running)]
    [InlineData(WorkingMode.Team, "团队任务已完成", ModeProgressState.Completed)]
    [InlineData(WorkingMode.Goal, "目标未完成", ModeProgressState.Failed)]
    public void RenderModeProgress_RendersOneUserFacingLine(
        WorkingMode mode,
        string message,
        ModeProgressState state)
    {
        var lines = ChatBlockRenderers.RenderModeProgress(
            new TuiModeProgress(mode, message, state, 2, 5),
            viewWidth: 48);

        lines.Should().ContainSingle();
        lines[0].FullText.Should().Contain(message);
        lines[0].FullText.Should().Contain("2/5");
        lines[0].FullText.Should().NotContain("RunId");
        lines[0].FullText.Should().NotContain("Budget");
        TextWidthHelper.GetDisplayWidth(lines[0].FullText).Should().BeLessThanOrEqualTo(48);
    }

    [Fact]
    public void RenderAgentCoordinationMessage_FormatsFromToContent()
    {
        var lines = ChatBlockRenderers.RenderAgentCoordinationMessage(
            "orchestrator", "purple",
            "researcher", "blue",
            "调研 Terminal.Gui 限制");
        var contentLine = lines.First(l => l.FullText.Contains("orchestrator"));
        contentLine.FullText.Should().Contain("│");
        contentLine.FullText.Should().Contain("→");
        contentLine.FullText.Should().Contain("researcher");
        contentLine.FullText.Should().Contain("调研 Terminal.Gui 限制");
    }

    [Fact]
    public void RenderAgentCoordinationMessage_IsMultiSegment()
    {
        var lines = ChatBlockRenderers.RenderAgentCoordinationMessage(
            "orchestrator", "purple", "researcher", "blue");
        var contentLine = lines.First(l => l.FullText.Contains("orchestrator"));
        contentLine.Segments.Should().NotBeNull();
        contentLine.Segments!.Count.Should().BeGreaterThanOrEqualTo(3); // pipe + from + arrow + to
    }

    [Fact]
    public void RenderAgentMessage_HasHeaderAndContent()
    {
        var lines = ChatBlockRenderers.RenderAgentMessage("executor", "orange", "done");
        lines.Should().Contain(l => l.FullText.Contains("Executor"));
        lines.Should().Contain(l => l.FullText.Contains("done"));
        var headerLine = lines.First(l => l.FullText.Contains("Executor"));
        headerLine.Segments.Should().NotBeNull();
        headerLine.Segments!.Should().Contain(s => s.Text.Contains("\u25b8"));
    }

    [Fact]
    public void RenderThinkingBlock_WithoutText_ShowsEllipsis()
    {
        var lines = ChatBlockRenderers.RenderThinkingBlock(null);
        lines[0].FullText.Should().Contain("思考");
    }

    [Fact]
    public void RenderThinkingBlock_WithText_ShowsText()
    {
        var lines = ChatBlockRenderers.RenderThinkingBlock("Considering options…");
        lines[0].FullText.Should().Contain("Considering options");
    }
}
