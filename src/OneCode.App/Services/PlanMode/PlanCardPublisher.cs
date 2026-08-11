namespace OneCode.App.Services.PlanMode;

using OneCode.App.Tui;
using OneCode.Core.PlanMode;

/// <summary>
/// Lightweight pub/sub bus for plan-card events flowing from the backend
/// (<see cref="Tools.CreatePlanTool"/>) to the TUI layer.
///
/// <para>
/// Why a separate type instead of putting the event on
/// <c>IPlanModeService</c>? The workflow service lives in
/// <c>OneCode.Core</c> and must not depend on the TUI layer (where
/// <see cref="PlanStep"/> is defined). This publisher lives in
/// <c>OneCode.App</c>, which can reference both Core and the TUI types,
/// so it is the natural bridge.
/// </para>
///
/// <para>
/// Registered as a singleton. <c>CreatePlanTool.SavePlanAsync</c> calls
/// <see cref="Publish"/> with <see cref="PlanCardPhase.Draft"/>; <c>SubmitPlanAsync</c>
/// calls it with <see cref="PlanCardPhase.PendingApproval"/>. The TUI host
/// subscribes via <see cref="PlanCreated"/> and renders a plan card through
/// <see cref="ReplShell.ShowPlanCard"/>——PendingApproval 阶段额外弹出 InlineSelector
/// 决策面板。
/// </para>
/// </summary>
public sealed class PlanCardPublisher
{
    /// <summary>Raised when the persisted workflow projection changes.</summary>
    public event Action<PlanWorkflow>? WorkflowChanged;

    /// <summary>
    /// Raised (on a background thread) when a plan is written. Subscribers
    /// must marshal onto the UI thread themselves.
    /// </summary>
    public event Action<string, IReadOnlyList<PlanStep>, PlanCardPhase>? PlanCreated;

    /// <summary>
    /// 是否存在订阅者（TUI 已接线决策通路）。Headless/Cron 场景下无订阅者，
    /// 调用方不应阻塞等待用户决策——这是 CreatePlanTool 判断 headless 的正确依据
    /// （publisher 在 DI 中始终注册，"publisher 是否为 null"不能作为判据）。
    /// </summary>
    public bool HasSubscribers => PlanCreated is not null;

    /// <summary>
    /// Notify subscribers that a new plan is available for review.
    /// phase 决定 TUI 渲染行为：Draft 仅展示卡片；PendingApproval 弹出决策面板。
    /// </summary>
    public void Publish(string title, IReadOnlyList<PlanStep> steps, PlanCardPhase phase)
    {
        PlanCreated?.Invoke(title, steps, phase);
    }

    public void Publish(PlanWorkflow workflow)
        => WorkflowChanged?.Invoke(workflow);
}
