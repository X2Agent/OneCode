using OneCode.App.Services.Agent;
using OneCode.Core.Coordinator;

namespace OneCode.App.Tui;

/// <summary>
/// 待办横条（<see cref="TodoStripView"/>）的宿主逻辑：快照进入、显示/隐藏与
/// 内容区高度让位。与 Plan 侧边栏（右停靠、数据驱动显隐）的关系只有一处：
/// 计划执行中且侧边栏可见时，横条不再重复显示计划预置的步骤待办。
/// </summary>
public sealed partial class ReplShell
{
    private readonly TodoStripView _todoStrip;

    /// <summary>最近一次收到的完整快照；侧边栏可见性变化时据此重算横条内容。</summary>
    private IReadOnlyList<TodoListItem> _todoItems = [];

    /// <summary>
    /// 用权威快照更新横条。空列表隐藏横条并归还对话区高度；
    /// 非空时按 <see cref="TodoStripView.RequiredRows"/> 让位。
    /// </summary>
    public void ShowTodoPanel(IReadOnlyList<TodoListItem> items)
    {
        _todoItems = items;
        RefreshTodoStrip();
    }

    /// <summary>更新 GOAL 模式子目标进度快照并刷新横条布局。</summary>
    public void ShowGoalPlan(
        IReadOnlyList<OneCode.Core.Goals.GoalStepSnapshot> steps,
        IReadOnlyList<OneCode.Core.Goals.GoalStepExecutionEvidence> executions,
        int currentIndex,
        OneCode.Core.Goals.GoalBudgetUsage? usage = null,
        OneCode.Core.Goals.GoalBudget? budget = null)
    {
        _todoStrip.UpdateGoalPlan(steps, executions, currentIndex, usage, budget);
        ApplyTodoStripLayout();
    }

    /// <summary>清空待办横条并隐藏（会话切换/关闭时调用）。</summary>
    public void ClearTodoPanel()
    {
        _todoItems = [];
        _todoStrip.ClearGoalPlan();
        RefreshTodoStrip();
    }

    /// <summary>
    /// 按当前侧边栏状态重算横条内容。计划侧边栏可见且持有步骤列表时（无论是执行中还是执行结束后的展示态），
    /// 侧边栏已完整呈现步骤进度，横条不再重复列出计划预置的步骤待办；侧边栏隐藏时横条是唯一进度面，保持完整。
    /// </summary>
    private void RefreshTodoStrip()
    {
        if (_activePlan is { Phase: PlanCardPhase.Completed } && _planSidebar.Visible)
        {
            _todoStrip.Update([]);
            ApplyTodoStripLayout();
            return;
        }

        _todoStrip.Update(PlanSidebarShowsStepProgress(out var stepTitles)
            ? _todoItems.Where(todo => !PlanTodoSeeder.IsSeededFor(todo.Title, stepTitles)).ToList()
            : _todoItems);
        ApplyTodoStripLayout();
    }

    private bool PlanSidebarShowsStepProgress(out IReadOnlyCollection<string> stepTitles)
    {
        if (_activePlan is { Steps.Count: > 0 } plan && _planSidebar.Visible)
        {
            stepTitles = plan.Steps.Select(step => step.Label).ToHashSet(StringComparer.Ordinal);
            return true;
        }

        stepTitles = [];
        return false;
    }

    private int ReservedBottomRows =>
        TuiSpacing.SessionContextBarHeight
        + TuiSpacing.StatusBarHeight
        + TuiSpacing.StatusBarTopGap
        + TuiSpacing.ChatInputContextGap
        + _chatInput.CurrentHeight;

    private void ApplyBottomStackLayout()
    {
        _chatInput.Height = _chatInput.CurrentHeight;
        _chatInput.Y = Pos.AnchorEnd(_chatInput.CurrentHeight + _chatInput.BottomOffset);
        _agentStatusBar.Y = Pos.Top(_chatInput) - 1;
        ApplyTodoStripLayout();
        SetNeedsLayout();
        SetNeedsDraw();
    }

    private void ApplyTodoStripLayout()
    {
        var rows = _todoStrip.RequiredRows;
        _todoStripRows = rows;
        _todoStrip.Visible = rows > 0;
        var reservedBottom = ReservedBottomRows;
        if (rows > 0)
        {
            _todoStrip.Height = rows;
            _todoStrip.Y = Pos.AnchorEnd(reservedBottom + rows);
        }

        // 横条从内容区底部"借"行：内容区下边界随横条行数及输入区动态高度下移，
        // 对话列宽度不变（仅高度变化，不需要按新宽度重排换行）。
        _contentZone.Height = Dim.Fill(reservedBottom + rows);
        _todoStrip.SetNeedsDraw();
    }
}
