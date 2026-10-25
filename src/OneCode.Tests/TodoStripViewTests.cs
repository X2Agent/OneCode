using OneCode.Core.Coordinator;
using OneCode.Tui;

namespace OneCode.Tests;

/// <summary>
/// 待办横条的行数契约：RequiredRows 决定 <c>ReplShell.ApplyTodoStripLayout</c>
/// 从内容区借走多少行——算错会挤压对话区或截断清单。
/// </summary>
public sealed class TodoStripViewTests
{
    [Fact]
    public void RequiredRows_EmptySnapshot_IsZero()
    {
        var view = new TodoStripView();

        view.Update([]);

        view.RequiredRows.Should().Be(0, "空清单必须整体隐藏，不占对话区高度");
    }

    [Fact]
    public void RequiredRows_SingleItem_HeaderPlusOneRow()
    {
        var view = new TodoStripView();

        view.Update([Item(1, "only")]);

        view.RequiredRows.Should().Be(2);
    }

    [Fact]
    public void RequiredRows_AtCap_HeaderPlusFourItemsNoOverflow()
    {
        var view = new TodoStripView();

        view.Update(Items(TodoStripView.MaxItems));

        view.RequiredRows.Should().Be(TodoStripView.MaxItems + 1);
    }

    [Fact]
    public void RequiredRows_OverCap_AddsSingleOverflowRow()
    {
        var view = new TodoStripView();

        view.Update(Items(TodoStripView.MaxItems + 1));

        view.RequiredRows.Should().Be(TodoStripView.MaxItems + 2,
            "超出封顶行数只追加一行汇总，不得逐条展开继续借对话区高度");
    }

    [Fact]
    public void GoalPlan_ToggleExpanded_ExpandsDetailsWithinMaxExpandedRows()
    {
        var view = new TodoStripView();
        var steps = new[]
        {
            new OneCode.Core.Goals.GoalStepSnapshot(
                1,
                "搜集资讯",
                "至少 8 条带日期来源的新闻",
                OneCode.Core.Goals.GoalStepState.Completed,
                ["WebSearch"],
                0,
                false,
                [],
                false,
                false,
                false),
            new OneCode.Core.Goals.GoalStepSnapshot(
                2,
                "生成仪表盘 HTML",
                "响应式深色主题且含 KPI 卡片",
                OneCode.Core.Goals.GoalStepState.InProgress,
                ["Write"],
                0,
                false,
                [],
                false,
                false,
                false),
        };
        var executions = new[]
        {
            new OneCode.Core.Goals.GoalStepExecutionEvidence(
                1,
                OneCode.Core.Goals.GoalStepState.Completed,
                Attempts: 1,
                InputTokens: 100,
                OutputTokens: 50,
                AgentOutput: "done",
                Evaluation: "Passed all checks",
                ChangedFiles: ["news.json"],
                ToolExecutions: [],
                Validations: [],
                Diagnostics: []),
        };

        view.UpdateGoalPlan(steps, executions, currentIndex: 1);
        var collapsedRows = view.RequiredRows;
        collapsedRows.Should().Be(3, "折叠态仅展示 1 行标题 + 2 个子目标摘要行");

        view.ToggleExpanded();
        view.IsExpanded.Should().BeTrue();
        view.RequiredRows.Should().BeGreaterThan(collapsedRows, "展开态必须包含子目标的验收标准、预期文件与评估结论明细行");
        view.RequiredRows.Should().BeLessThanOrEqualTo(TodoStripView.MaxExpandedRows + 2);
    }

    [Fact]
    public void HeaderText_UsesDynamicShortcutOrDefaultCtrlO()
    {
        var defaultView = new TodoStripView();
        defaultView.Update([Item(1, "task")]);
        defaultView.GetHeaderTextForTest().Should().Contain("[Ctrl+O 展开详情]");

        var currentShortcut = "Alt+P";
        var customView = new TodoStripView(() => currentShortcut);
        customView.Update([Item(1, "task")]);
        customView.GetHeaderTextForTest().Should().Contain("[Alt+P 展开详情]");

        customView.ToggleExpanded();
        customView.GetHeaderTextForTest().Should().Contain("[Alt+P 折叠]");
    }

    [Fact]
    public void GoalPlan_SupersededByReplan_RendersFailedGlyphAndReplannedBadgeInsteadOfSkippedCircle()
    {
        var view = new TodoStripView();
        var steps = new[]
        {
            new OneCode.Core.Goals.GoalStepSnapshot(
                1,
                "初始步骤失败",
                "验证通过",
                OneCode.Core.Goals.GoalStepState.Skipped,
                ["*"],
                0,
                false,
                [],
                false,
                false,
                false,
                SupersededByReplan: true),
            new OneCode.Core.Goals.GoalStepSnapshot(
                2,
                "重规划步骤",
                "验证通过",
                OneCode.Core.Goals.GoalStepState.InProgress,
                ["*"],
                0,
                false,
                [],
                false,
                false,
                false),
        };
        var executions = new[]
        {
            new OneCode.Core.Goals.GoalStepExecutionEvidence(
                1,
                OneCode.Core.Goals.GoalStepState.Failed,
                Attempts: 3,
                InputTokens: 100,
                OutputTokens: 50,
                AgentOutput: "",
                Evaluation: "Exhausted retry attempts",
                ChangedFiles: [],
                ToolExecutions: [],
                Validations: [],
                Diagnostics: []),
        };

        view.UpdateGoalPlan(steps, executions, currentIndex: 1);
        var lines = view.GetContentLineTextsForTest();

        lines[0].Should().Contain("✗ #1 初始步骤失败 (3/3轮) [已重规划]",
            "被重规划替代的失败步骤不得渲染为普通的 ○ 待办，必须保留 ✗ 与 [已重规划] 标记");
        lines[1].Should().Contain("▶ #2 重规划步骤 (执行中)");
    }

    private static TodoListItem Item(int id, string title, bool isComplete = false)
        => new(id, title, null, isComplete);

    private static IReadOnlyList<TodoListItem> Items(int count)
        => Enumerable.Range(1, count).Select(i => Item(i, $"item {i}")).ToList();
}
