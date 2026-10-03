namespace OneCode.App.Tui;

/// <summary>Plan card decision from keyboard shortcut.</summary>
public enum PlanCardDecision { Approve, Reject, Edit }

/// <summary>
/// Plan card lifecycle phase — tracks the plan from submission through user
/// approval and execution.
/// </summary>
public enum PlanCardPhase
{
    /// <summary>
    /// SubmitPlan 已返回，但当前 Plan Run 尚未完成持久化和协议校验。
    /// 仅展示提交中状态，不开放审批。
    /// </summary>
    Finalizing,

    /// <summary>
    /// Plan Run 已完整闭合并通过协议校验，允许用户审批。
    /// </summary>
    PendingApproval,

    /// <summary>批准快照已冻结，正在幂等启动新的 Build Run。</summary>
    StartingExecution,

    /// <summary>Build Run 正在执行批准的结构化步骤。</summary>
    Executing,

    /// <summary>Build 工作已结束，正在验证验收条件。</summary>
    Verifying,

    /// <summary>全部步骤和验证门禁已通过。</summary>
    Completed,

    /// <summary>工作流执行或验证失败。</summary>
    Failed,

    /// <summary>工作流已取消。</summary>
    Cancelled,
}

/// <summary>Mutable state for an active plan card awaiting user decision.</summary>
internal sealed class PlanCardState(
    string title,
    List<PlanStep> steps,
    PlanCardPhase phase,
    string? markdown = null,
    string? documentPath = null,
    bool isPendingRevision = false)
{
    public string Title { get; } = title;
    public List<PlanStep> Steps { get; } = steps;
    public PlanCardPhase Phase { get; } = phase;
    public string? Markdown { get; } = markdown;

    /// <summary>Persisted markdown document path shown on the card so users can locate the plan file.</summary>
    public string? DocumentPath { get; } = documentPath;

    /// <summary>
    /// 用户已选择「输入修改意见」——侧边栏冻结当前内容、标题切换为待修改态，
    /// 且 <see cref="ReplShell.RenderActivePlanCard"/> 不再叠加 Phase 后缀，直至下一次 ShowPlanCard 整体替换。
    /// </summary>
    public bool IsPendingRevision { get; } = isPendingRevision;
}

public enum PlanStepStatus { Pending = 0, Current = 1, Done = 2, }

/// <summary>
/// A single step in a plan card.
/// </summary>
/// <param name="Label">Short label shown as the primary line.</param>
/// <param name="Content">Optional longer description shown as a secondary line under the label.</param>
/// <param name="Assignee">Optional agent name shown as → name on the right.</param>
/// <param name="Status">Pending / Current / Done. Controls color and strikethrough.</param>
public sealed record PlanStep(
    string Label,
    string? Content = null,
    string? Assignee = null,
    PlanStepStatus Status = PlanStepStatus.Pending);
