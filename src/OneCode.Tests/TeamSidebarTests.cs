using OneCode.App.Tui;
using OneCode.Core.Coordinator;

namespace OneCode.Tests;

/// <summary>
/// TEAM 侧边栏重构的行为守护：
/// 1. <see cref="TeamRunSnapshot"/> 状态机——Team 事件流 → 各区数据（任务/决策/文件/门禁/节点）；
/// 2. <see cref="ChatBlockRenderers.RenderTeamSidebar"/>——纯显示约束（无任何键位提示文字）
///    与行格式；
/// 3. 终态后新事件触发整体重置；非 TEAM 成员的文件变更不入快照。
/// </summary>
public sealed class TeamSidebarTests
{
    [Fact]
    public void Snapshot_ClarificationFlow_RecordsDecisionAndMilestone()
    {
        var snapshot = new TeamRunSnapshot();
        snapshot.Apply(new TuiTeamProgress("团队 'impl' 需要澄清 5 个问题后才能规划", [], null, "impl"));
        snapshot.Phase.Should().Be("等待澄清");
        snapshot.TeamName.Should().Be("impl");
        snapshot.ToContent().Decisions.Should().BeEmpty(); // 进行中的交互不进快照

        snapshot.Apply(new TuiTeamUserResponse("impl", "模拟命令行工具运行，用于压力测试"));
        var content = snapshot.ToContent();
        snapshot.Phase.Should().Be("规划中");
        content.Decisions.Should().ContainSingle(d => d.Answer.Contains("压力测试"));
        content.Milestones.Should().Contain("澄清完成");
    }

    [Fact]
    public void Snapshot_ApprovalThenTaskProgress_BuildsTaskListAndMilestones()
    {
        var snapshot = new TeamRunSnapshot();
        snapshot.Apply(new TuiTeamPlanApproval("impl", "摘要",
            ["实现解析器", "运行测试"], ["compile", "test"]));

        var pending = snapshot.ToContent();
        snapshot.Phase.Should().Be("等待审批");
        pending.Tasks.Should().HaveCount(2);
        pending.Tasks.Should().OnlyContain(t => t.Status == "pending");
        pending.Gates.Should().HaveCount(2);

        snapshot.Apply(new TuiTeamTaskProgress("t1", "实现解析器", "executor", null, 0, 2));
        snapshot.Apply(new TuiTeamTaskProgress("t1", "实现解析器", "executor", "Succeeded", 2, 2));

        var content = snapshot.ToContent();
        snapshot.Phase.Should().Be("执行中");
        content.Milestones.Should().Contain("计划已批准，开始执行");
        var done = content.Tasks.Should().ContainSingle(t => t.TaskId == "t1").Subject;
        done.Status.Should().Be("done");
        done.Completed.Should().Be(2);
        done.Total.Should().Be(2);
    }

    [Fact]
    public void Snapshot_AggregatesTeamFileChanges_IgnoresNonTeamChanges()
    {
        var snapshot = new TeamRunSnapshot();
        snapshot.Apply(new TuiTeamTaskProgress("t1", "实现解析器", "executor", null, 0, 1));

        snapshot.Apply(new TuiFileChange("parser.ts", ["a", "b", "c"], ["x"], "executor-1"));
        snapshot.Apply(new TuiFileChange("parser.ts", ["d"], [], "executor-1"));
        snapshot.Apply(new TuiFileChange("tests.ts", ["t1", "t2"], [], "executor-2"));
        // 主对话路径（AgentName 为 null）的文件修改不属于团队运行，不入快照
        snapshot.Apply(new TuiFileChange("unrelated.cs", ["z"], [], null));

        var files = snapshot.ToContent().Files;
        files.Should().HaveCount(2);
        var parser = files.Should().ContainSingle(f => f.FileName == "parser.ts").Subject;
        parser.Added.Should().Be(4);
        parser.Removed.Should().Be(1);
        parser.Contributors.Should().Be("executor-1");
    }

    [Fact]
    public void Snapshot_Delivery_TerminalStateWithGateStatuses()
    {
        var snapshot = new TeamRunSnapshot();
        snapshot.Apply(new TuiTeamPlanApproval("impl", "摘要",
            ["实现解析器"], ["compile"]));
        snapshot.Apply(MakeDelivery(committed: true));

        snapshot.IsTerminal.Should().BeTrue();
        snapshot.Phase.Should().Be("已完成");
        var content = snapshot.ToContent();
        content.Gates.Should().ContainSingle(g => g.Status == "Passed");
        content.Milestones.Should().Contain(m => m.Contains("交付已提交"));
    }

    [Fact]
    public void Snapshot_NewRunAfterTerminal_ResetsSnapshot()
    {
        var snapshot = new TeamRunSnapshot();
        snapshot.Apply(new TuiTeamUserResponse("impl", "回答"));
        snapshot.Apply(MakeDelivery(committed: true));
        snapshot.IsTerminal.Should().BeTrue();

        // 新一次运行的澄清事件 → 整体重置，旧决策不残留
        snapshot.Apply(new TuiTeamProgress("团队 'code-review' 需要澄清 2 个问题后才能规划", [], null, "code-review"));
        var content = snapshot.ToContent();
        snapshot.IsTerminal.Should().BeFalse();
        snapshot.TeamName.Should().Be("code-review");
        content.Decisions.Should().BeEmpty();
        content.Tasks.Should().BeEmpty();
        content.Milestones.Should().BeEmpty();
    }

    [Fact]
    public void Snapshot_ProgressWithoutTeamName_KeepsPreviousTeamName()
    {
        // 结构化字段缺失时不做任何猜测（尤其不得从 Header 展示文案反解），保持原值。
        var snapshot = new TeamRunSnapshot();
        snapshot.Apply(new TuiTeamProgress("等待澄清", [], null, "impl"));
        snapshot.Apply(new TuiTeamProgress("仍在澄清", [], null));
        snapshot.TeamName.Should().Be("impl");
    }

    [Fact]
    public void RenderTeamSidebar_RendersAllSections_WithoutAnyKeybindingHints()
    {
        var snapshot = new TeamRunSnapshot();
        snapshot.Apply(new TuiTeamProgress("团队 'impl' 需要澄清 2 个问题后才能规划", [], null, "impl"));
        snapshot.Apply(new TuiTeamUserResponse("impl", "模拟命令行工具运行"));
        snapshot.Apply(new TuiTeamPlanApproval("impl", "摘要", ["实现解析器"], ["compile"]));
        snapshot.Apply(new TuiTeamTaskProgress("t1", "实现解析器", "executor", "Succeeded", 1, 1));
        snapshot.Apply(new TuiFileChange("parser.ts", ["a", "b"], ["c"], "executor-1"));
        snapshot.Apply(MakeDelivery(committed: true));

        var lines = ChatBlockRenderers.RenderTeamSidebar(snapshot.ToContent(), width: 48);
        var text = string.Join("\n", lines.Select(l => l.FullText));

        // 五区齐全（标题带图标与汇总计数）
        text.Should().Contain("任务");
        text.Should().Contain("澄清决策");
        text.Should().Contain("模拟命令行工具运行");
        text.Should().Contain("文件变更 (1)");
        text.Should().Contain("parser.ts");
        text.Should().Contain("+2 -1");
        text.Should().Contain("质量门禁");
        text.Should().Contain("关键节点");
        text.Should().Contain("交付已提交");

        // 纯显示约束：不得出现任何键位/操作提示文字
        text.Should().NotContain("Enter");
        text.Should().NotContain("Esc");
        text.Should().NotContain("Alt+");
        text.Should().NotContain("Ctrl+");
        text.Should().NotContain("点击");
        text.Should().NotContain("请选择");
    }

    [Fact]
    public void RenderTeamSidebar_EmptySnapshot_RendersNoSectionHeaders()
    {
        var lines = ChatBlockRenderers.RenderTeamSidebar(new TeamRunSnapshot().ToContent(), width: 48);
        var text = string.Join("\n", lines.Select(l => l.FullText));
        text.Should().NotContain("── ");
    }

    [Fact]
    public void RenderTeamSidebar_TasksRunningFirst_DoneLast_SectionTitleCarriesProgress()
    {
        var snapshot = new TeamRunSnapshot();
        snapshot.Apply(new TuiTeamPlanApproval("impl", "摘要", ["t-done", "t-run", "t-pending"], []));
        snapshot.Apply(new TuiTeamTaskProgress("t-done", "t-done", "executor", "Succeeded", 1, 1));
        snapshot.Apply(new TuiTeamTaskProgress("t-run", "t-run", "executor", null, 0, 1));

        var texts = ChatBlockRenderers.RenderTeamSidebar(snapshot.ToContent(), width: 48)
            .Select(l => l.FullText).ToList();

        // 标题汇总计数：1 已结束 / 3 总数，一眼看到整体进度
        texts.Should().Contain(t => t.Contains("任务 (1/3)"));

        // 重点前置：进行中 → 待执行 → 已完成
        var indexOf = (string fragment) => texts.FindIndex(t => t.Contains(fragment));
        var running = indexOf("t-run");
        var pending = indexOf("t-pending");
        var done = indexOf("t-done");
        running.Should().BeGreaterThan(-1);
        pending.Should().BeGreaterThan(-1);
        done.Should().BeGreaterThan(-1);
        running.Should().BeLessThan(pending);
        pending.Should().BeLessThan(done);
    }

    [Fact]
    public void RenderTeamSidebar_CjkContent_EveryRowFitsWithinPanelWidth()
    {
        // 回归：旧 TruncateToFit 按 text.Length 截断，CJK 双宽字符会溢出面板。
        var snapshot = new TeamRunSnapshot();
        snapshot.Apply(new TuiTeamUserResponse("impl", string.Concat(Enumerable.Repeat("很长的中文回答", 30))));

        var lines = ChatBlockRenderers.RenderTeamSidebar(snapshot.ToContent(), width: 32);
        lines.Select(l => TextWidthHelper.GetDisplayWidth(l.FullText))
            .Should().OnlyContain(w => w <= 32);
    }

    private static TuiTeamDelivery MakeDelivery(bool committed)
    {
        var gates = new List<QualityGateResult>
        {
            new("compile", QualityGateKind.Build, Required: true, QualityGateStatus.Passed, "ok", [], TimeSpan.Zero),
        };
        return new TuiTeamDelivery(new DeliveryReport(
            new TeamRunId("r1"), "impl", committed, "done",
            Tasks: [], Gates: gates,
            Changes: new ChangeSetSummary([], 0, 0),
            Risks: [], GeneratedAt: DateTimeOffset.Now));
    }
}
