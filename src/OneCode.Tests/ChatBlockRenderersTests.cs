using OneCode.App.Tui;
using OneCode.Core.Coordinator;

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
    public void RenderModeBanner_Team_MentionsYamlFixedMode()
    {
        var lines = ChatBlockRenderers.RenderModeBanner(WorkingMode.Team);
        lines[1].FullText.Should().Contain("TEAM");
        lines[1].FullText.Should().Contain("team.yaml");
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

        // 无归属（非 Team 路径）不出现 by 标注。
        var withoutAgent = ChatBlockRenderers.RenderDiffBlock(
            "B.cs", new[] { "+ x" }, []);
        withoutAgent.Should().NotContain(l => l.FullText.Contains("by "));
    }

    [Fact]
    public void MapOrchestrationEvent_TeamTaskProgress_MapsAllFields()
    {
        var mapped = TuiEventMapper.MapOrchestrationEventToTuiEvent(
            new OrchestrationEvent.TeamTaskProgress(
                "t1", "实现解析器", "executor", null,
                CompletedTasks: 2, TotalTasks: 5, ActiveTasks: 1, BlockedTasks: 0));
        var progress = mapped.Should().BeOfType<TuiTeamTaskProgress>().Subject;
        progress.TaskId.Should().Be("t1");
        progress.TaskTitle.Should().Be("实现解析器");
        progress.AssigneeRole.Should().Be("executor");
        progress.Status.Should().BeNull();
        progress.CompletedTasks.Should().Be(2);
        progress.TotalTasks.Should().Be(5);
        progress.ActiveTasks.Should().Be(1);

        var done = TuiEventMapper.MapOrchestrationEventToTuiEvent(
            new OrchestrationEvent.TeamTaskProgress(
                "t1", "实现解析器", "executor", TeamTaskStatus.Succeeded.ToString(),
                CompletedTasks: 3, TotalTasks: 5))
            .Should().BeOfType<TuiTeamTaskProgress>().Subject;
        done.Status.Should().Be("Succeeded");
    }

    [Fact]
    public void MapOrchestrationEvent_TeamClarificationRequest_MapsToStructuredProgress()
    {
        // 澄清请求不再映射为 TuiError（会被截断成单行红字），
        // 而是结构化 TuiTeamProgress：标题 + 编号问题清单，多行完整显示。
        var mapped = TuiEventMapper.MapOrchestrationEventToTuiEvent(
            new OrchestrationEvent.TeamClarificationRequest(
                new TeamRunId("r1"), "impl", "目标",
                ["第一个里程碑想验证什么核心能力？", "交付物是什么？"]));
        var progress = mapped.Should().BeOfType<TuiTeamProgress>().Subject;
        progress.Header.Should().Contain("impl").And.Contain("2 个问题");
        progress.Tasks.Should().HaveCount(2);
        progress.Tasks[0].Detail.Should().Contain("里程碑");
    }

    [Fact]
    public void MapOrchestrationEvent_AgentEvents_CarryAgentAttribution()
    {
        var fileChange = TuiEventMapper.MapOrchestrationEventToTuiEvent(
                new OrchestrationEvent.FileChanged("executor-1", "A.cs", ["+ a"], []))
            .Should().BeOfType<TuiFileChange>().Subject;
        fileChange.AgentName.Should().Be("executor-1");

        var toolStart = TuiEventMapper.MapOrchestrationEventToTuiEvent(
                new OrchestrationEvent.ToolStart("executor-1", "id1", "read_file"))
            .Should().BeOfType<TuiToolStart>().Subject;
        toolStart.AgentName.Should().Be("executor-1");

        var toolDone = TuiEventMapper.MapOrchestrationEventToTuiEvent(
                new OrchestrationEvent.ToolDone("executor-1", "read_file", IsError: false, ToolId: "id1"))
            .Should().BeOfType<TuiToolDone>().Subject;
        toolDone.AgentName.Should().Be("executor-1");
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
        var lines = ChatBlockRenderers.RenderPlanCard("重构计划", steps);
        lines.Should().Contain(l => l.FullText.Contains("重构计划"));
        lines.Should().Contain(l => l.FullText.Contains("→ executor"));
        lines.Should().Contain(l => l.FullText.Contains("→ reviewer"));
    }

    [Fact]
    public void RenderPlanCard_PendingApproval_ShowsFullPlan()
    {
        var lines = ChatBlockRenderers.RenderPlanCard(
            "最终计划",
            [new PlanStep("修改渲染链路", "统一处理 Unicode")],
            viewWidth: 60,
            markdown: "# 最终计划\n\n## 验证\n\n- 运行测试");
        var text = string.Join("\n", lines.Select(line => line.FullText));

        text.Should().Contain("完整计划");
        text.Should().Contain("验证");
        text.Should().Contain("运行测试");
        // 审批操作引导由对话流中的 InlineSelector 决策面板承担——侧边栏卡片
        // 不再渲染"在下方选择…"提示（"下方"指对话流，出现在侧边栏是语义错位）。
        text.Should().NotContain("在下方选择");
        lines.Should().OnlyContain(line => TextWidthHelper.GetDisplayWidth(line.FullText) <= 60);
    }

    // 提交/审批阶段渲染完整计划全文，但不含任何硬编码辅助说明——辅助性文字由
    // LLM 在对话流中提供，侧边栏只保留纯计划内容（回归守卫：删除后若有人
    // 重新在卡片里加入操作引导/提示文字，此测试会失败）。
    [Fact]
    public void RenderPlanCard_ShowsOnlyPlanContentWithoutAuxiliaryText()
    {
        var lines = ChatBlockRenderers.RenderPlanCard(
            "草稿计划",
            [new PlanStep("第一步")],
            viewWidth: 60,
            markdown: "# 草稿计划\n\n## 思路\n\n- 先扫描再改写");
        var text = string.Join("\n", lines.Select(line => line.FullText));

        text.Should().Contain("完整计划");
        text.Should().Contain("先扫描再改写");
        text.Should().NotContain("计划整理中");
        text.Should().NotContain("输入修改意见");
        text.Should().NotContain("批准并执行");
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
    public void RenderAgentMessage_HasHeaderAndContent()
    {
        var lines = ChatBlockRenderers.RenderAgentMessage("executor", "orange", "done");
        lines.Should().Contain(l => l.FullText.Contains("Executor"));
        lines.Should().Contain(l => l.FullText.Contains("done"));
        var headerLine = lines.First(l => l.FullText.Contains("Executor"));
        headerLine.Segments.Should().NotBeNull();
        headerLine.Segments!.Should().Contain(s => s.Text.Contains("\u25b8"));
    }
}
