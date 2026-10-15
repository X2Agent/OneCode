namespace OneCode.App.Tui;

/// <summary>
/// Plan sidebar interaction for <see cref="ReplShell"/>。
/// 计划内容渲染在右侧 <see cref="PlanSidebarView"/>，不再进入对话流；
/// PendingApproval 阶段弹出 InlineSelector 决策面板（对话流内），自动接管键盘
/// （SetInteractionSuspended）；其余阶段仅展示侧边栏不弹决策面板。
/// </summary>
public sealed partial class ReplShell
{
    private PlanCardState? _activePlan;

    internal PlanCardState? ActivePlan => _activePlan;

    /// <summary>Raised when the user approves/rejects/edits a plan card via keyboard.</summary>
    public event Action<PlanCardDecision>? PlanDecisionMade;

    /// <summary>
    /// Show the plan in the right sidebar. <see cref="PlanCardPhase.PendingApproval"/>
    /// 额外弹出 InlineSelector 决策面板（批准/拒绝/修改），自动接管键盘；
    /// 其余阶段仅更新侧边栏内容，不响应任何决策输入。
    /// </summary>
    public void ShowPlanCard(
        string title,
        IReadOnlyList<PlanStep> steps,
        PlanCardPhase phase,
        string? markdown = null,
        string? documentPath = null)
    {
        // 侧边栏一旦展开即保持可见（直至计划清除/新会话），后续更新只刷新内容；
        // PendingApproval 额外弹出 InlineSelector 决策面板。
        _activePlan = new PlanCardState(title, steps.ToList(), phase, markdown, documentPath);
        RenderActivePlanCard();
        SetPlanSidebarVisible(true);

        if (phase is PlanCardPhase.Completed or PlanCardPhase.Failed or PlanCardPhase.Cancelled)
        {
            // 终态保留侧边栏展示结果；下一次计划开始时内容整体替换。
            return;
        }

        if (phase != PlanCardPhase.PendingApproval)
        {
            // 非审批阶段：仅展示侧边栏，不弹决策面板。用户无法提前决策，消除 _pendingDecision 竞态。
            return;
        }

        // PendingApproval：确保侧边栏可见（用户需要看到完整计划才能决策），
        // 并弹出 InlineSelector 决策面板。
        SetPlanSidebarVisible(true);
        List<InlineSelectorOption> options = [
            new("approve", "批准并执行", "冻结当前计划，切换到 Build 模式立即开始执行"),
            new("edit", "输入修改意见", "返回输入框，用自然语言说明要调整的内容"),
            new("reject", "拒绝计划", "保持在 Plan 模式，重新规划当前方案"),
        ];
        var selector = new InlineSelector("请审批右侧计划", options);
        ShowInlineSelector(selector);

        _ = selector.ResultTask.ContinueWith(t =>
        {
            _app.Invoke(() =>
            {
                DismissInlineSelector();
                // Esc 取消（Dismissed）视为 Reject——保持 Plan 模式，等待 LLM 修订
                var decision = (t.IsCompletedSuccessfully && !t.Result.IsDismissed)
                    ? t.Result.SelectedId switch
                    {
                        "approve" => PlanCardDecision.Approve,
                        "reject" => PlanCardDecision.Reject,
                        "edit" => PlanCardDecision.Edit,
                        _ => PlanCardDecision.Reject,
                    }
                    : PlanCardDecision.Reject;

                PlanDecisionMade?.Invoke(decision);
                if (decision == PlanCardDecision.Edit)
                {
                    if (_activePlan is not null)
                    {
                        // 冻结当前内容并标记待修改态：标题更新、不再叠加 Phase 后缀，
                        // 严禁 ClearPlan() 盲改——用户需对照原计划反馈。
                        _activePlan = new PlanCardState(
                            "📋 实施计划 · 待修改",
                            _activePlan.Steps,
                            _activePlan.Phase,
                            _activePlan.Markdown,
                            _activePlan.DocumentPath,
                            isPendingRevision: true);
                        RenderActivePlanCard();
                        SetPlanSidebarVisible(true);
                    }
                    _chatInput.SetInputText("请按以下意见修改计划：");
                    _chatInput.FocusInput();
                }
                else if (decision == PlanCardDecision.Reject)
                {
                    ClearPlan();
                }
            });
        }, TaskScheduler.Default);
    }

    private void RenderActivePlanCard()
    {
        if (_activePlan is not { } plan)
            return;

        var displayTitle = FormatPlanHeaderTitle(plan, _planSidebar.CurrentWidth);
        // 侧边栏内容按当前面板宽度（可拖动调整）换行渲染；只含计划内容，
        // 辅助性说明由 LLM 在对话流中提供。
        var lines = ChatBlockRenderers.RenderPlanCard(
            displayTitle,
            plan.Steps,
            _planSidebar.CurrentWidth - 2,
            markdown: plan.Markdown,
            documentPath: plan.DocumentPath,
            // 侧边栏顶部已由 SidebarViewBase 绘制固定标题行，内容里不再重复标题。
            includeHeader: false);
        _planSidebar.Update(lines, displayTitle);
        // 横条是否还需要列出步骤待办取决于计划阶段与侧边栏可见性，两者在此一并变化。
        RefreshTodoStrip();
    }

    /// <summary>
    /// 组装侧边栏顶部标题并按可用列宽优先保留阶段状态后缀（如 <c> · 已完成</c>）。
    /// 长标题若直接拼接状态后再交由 <see cref="SidebarViewBase"/> 从右截断，会把阶段状态裁成 <c> · …</c>。
    /// </summary>
    internal static string FormatPlanHeaderTitle(PlanCardState plan, int sidebarWidth)
    {
        if (plan.IsPendingRevision)
            return plan.Title;

        var suffix = plan.Phase switch
        {
            PlanCardPhase.Finalizing => " · 正在确认",
            PlanCardPhase.PendingApproval => " · 等待审批",
            PlanCardPhase.StartingExecution => " · 准备执行",
            PlanCardPhase.Executing => " · 正在执行",
            PlanCardPhase.Completed => " · 已完成",
            PlanCardPhase.Failed => " · 执行失败",
            PlanCardPhase.Cancelled => " · 已取消",
            _ => string.Empty,
        };
        if (suffix.Length == 0)
            return plan.Title;

        // SidebarViewBase 标题行前缀 " 📋 " 占 4 列，右侧留 1 列边距，共 5 列固定开销。
        const int headerChromeColumns = 5;
        var availableWidth = sidebarWidth - headerChromeColumns;
        var suffixWidth = TextWidthHelper.GetDisplayWidth(suffix);
        var titleBudget = availableWidth - suffixWidth;
        if (titleBudget <= 1)
            return $"{plan.Title}{suffix}";

        var truncatedTitle = TextWidthHelper.GetDisplayWidth(plan.Title) > titleBudget
            ? TextWidthHelper.TruncateByWidth(plan.Title, titleBudget)
            : plan.Title;
        return $"{truncatedTitle}{suffix}";
    }

    /// <summary>Clears the plan and hides the sidebar.</summary>
    public void ClearPlan()
    {
        _activePlan = null;
        _planSidebar.ClearContent();
        SetPlanSidebarVisible(false);
        RefreshTodoStrip();
    }
}
