namespace OneCode.App.Tui;

/// <summary>
/// Plan sidebar interaction for <see cref="ReplShell"/> (design-spec §4.2)。
/// 计划内容渲染在右侧 <see cref="PlanSidebarView"/>，不再进入对话流；
/// PendingApproval 阶段弹出 InlineSelector 决策面板（对话流内），自动接管键盘
/// （SetInteractionSuspended）；其余阶段仅展示侧边栏不弹决策面板。
/// </summary>
public sealed partial class ReplShell
{
    private PlanCardState? _activePlan;

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
        // 新计划第一次出现（前一状态为空）自动展开侧边栏；同一计划的后续更新
        // 尊重用户 Ctrl+G 的显式收起，不强制重新展开——PendingApproval 例外：
        // 审批决策必须能看到完整计划。
        var isNewPlan = _activePlan is null;
        _activePlan = new PlanCardState(title, steps.ToList(), phase, markdown, documentPath);
        RenderActivePlanCard();
        if (isNewPlan)
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
        var options = new List<InlineSelectorOption>
        {
            new("approve", "批准并执行", "冻结当前计划，切换到 Build 模式立即开始执行"),
            new("edit", "输入修改意见", "返回输入框，用自然语言说明要调整的内容"),
            new("reject", "拒绝计划", "保持在 Plan 模式，重新规划当前方案"),
        };
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
                    _chatInput.SetInputText("请按以下意见修改计划：");
                    _chatInput.FocusInput();
                }
                ClearPlan();
            });
        }, TaskScheduler.Default);
    }

    private void RenderActivePlanCard()
    {
        if (_activePlan is not { } plan)
            return;

        var displayTitle = plan.Phase switch
        {
            PlanCardPhase.Finalizing => $"{plan.Title} · 正在确认",
            PlanCardPhase.PendingApproval => $"{plan.Title} · 等待审批",
            PlanCardPhase.StartingExecution => $"{plan.Title} · 准备执行",
            PlanCardPhase.Executing => $"{plan.Title} · 正在执行",
            PlanCardPhase.Verifying => $"{plan.Title} · 正在验证",
            PlanCardPhase.Completed => $"{plan.Title} · 已完成",
            PlanCardPhase.Failed => $"{plan.Title} · 执行失败",
            PlanCardPhase.Cancelled => $"{plan.Title} · 已取消",
            _ => plan.Title,
        };
        // 侧边栏内容按当前面板宽度（可拖动调整）换行渲染；只含计划内容，
        // 辅助性说明由 LLM 在对话流中提供。
        var lines = ChatBlockRenderers.RenderPlanCard(
            displayTitle,
            plan.Steps,
            _planSidebar.CurrentWidth - 2,
            markdown: plan.Markdown,
            documentPath: plan.DocumentPath);
        _planSidebar.Update(lines, displayTitle);
    }

    /// <summary>Clears the plan and hides the sidebar.</summary>
    public void ClearPlan()
    {
        _activePlan = null;
        _planSidebar.ClearContent();
        SetPlanSidebarVisible(false);
    }
}
