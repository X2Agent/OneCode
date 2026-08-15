namespace OneCode.App.Tui;

/// <summary>
/// Plan card interaction for <see cref="ReplShell"/> (design-spec §4.2)。
/// PendingApproval 阶段弹出 InlineSelector 决策面板，自动接管键盘
/// （SetInteractionSuspended）；Draft 阶段仅展示卡片不弹决策面板。
/// </summary>
public sealed partial class ReplShell
{
    private PlanCardState? _activePlan;

    /// <summary>Raised when the user approves/rejects/edits a plan card via keyboard.</summary>
    public event Action<PlanCardDecision>? PlanDecisionMade;

    /// <summary>
    /// Show a plan card. <see cref="PlanCardPhase.PendingApproval"/> 额外弹出
    /// InlineSelector 决策面板（批准/拒绝/修改），自动接管键盘；<see cref="PlanCardPhase.Draft"/>
    /// 仅展示卡片，不响应任何决策输入。
    /// </summary>
    public void ShowPlanCard(
        string title,
        IReadOnlyList<PlanStep> steps,
        PlanCardPhase phase,
        string? markdown = null)
    {
        _activePlan = new PlanCardState(title, steps.ToList(), phase, markdown);
        RenderActivePlanCard();

        if (phase is PlanCardPhase.Completed or PlanCardPhase.Failed or PlanCardPhase.Cancelled)
        {
            // Seal the terminal card as transcript history so a later plan starts a new card.
            _activePlan = null;
            _transcript.ClearActivePlanCard();
            return;
        }

        if (phase != PlanCardPhase.PendingApproval)
        {
            // Draft：仅展示卡片，不弹决策面板。用户无法提前决策，消除 _pendingDecision 竞态。
            return;
        }

        // PendingApproval：弹出 InlineSelector 决策面板。
        // InlineSelector 通过 _chatInput.SetInteractionSuspended(true) 自动接管键盘，
        // 无需依赖 prompt 失焦——彻底消除焦点冲突。
        var options = new List<InlineSelectorOption>
        {
            new("approve", "批准并执行", "冻结当前计划，切换到 Build 模式立即开始执行"),
            new("edit", "输入修改意见", "返回输入框，用自然语言说明要调整的内容"),
            new("reject", "拒绝计划", "保持在 Plan 模式，重新规划当前方案"),
        };
        var selector = new InlineSelector("请审批以上计划", options);
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
                ClearPlan(sealCard: false);
            });
        }, TaskScheduler.Default);
    }

    private void RenderActivePlanCard()
    {
        if (_activePlan is not { } plan)
            return;

        var displayTitle = plan.Phase switch
        {
            PlanCardPhase.Draft => $"{plan.Title} · 正在整理",
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
        var lines = ChatBlockRenderers.RenderPlanCard(
            displayTitle,
            plan.Steps,
            ContentWidth,
            showActionButtons: false,
            showApprovalGuidance: plan.Phase == PlanCardPhase.PendingApproval,
            markdown: plan.Markdown);
        _transcript.UpdatePlanCard(lines);
    }

    /// <summary>Clears the approval interaction; optionally seals the card as transcript history.</summary>
    public void ClearPlan(bool sealCard = true)
    {
        _activePlan = null;
        if (sealCard)
            _transcript.ClearActivePlanCard();
    }
}
