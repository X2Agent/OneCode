namespace OneCode.App.Services.PlanMode;

/// <summary>计划卡的用户决策。</summary>
public enum PlanCardDecision { Approve, Reject, Edit }

/// <summary>
/// 计划卡生命周期阶段 —— 跟踪计划从提交、审批到执行的推进状态。
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

    /// <summary>Build Run 正常结束且最终验证通过。</summary>
    Completed,

    /// <summary>工作流执行失败。</summary>
    Failed,

    /// <summary>工作流已取消。</summary>
    Cancelled,
}

/// <summary>计划步骤的渲染状态。</summary>
public enum PlanStepStatus { Pending = 0, Current = 1, Done = 2, }

/// <summary>
/// 计划卡中的单个步骤。
/// </summary>
/// <param name="Label">作为主行显示的短标签。</param>
/// <param name="Content">可选的长描述，作为标签下方的次行。</param>
/// <param name="Assignee">可选的代理名，右侧以 → name 形式显示。</param>
/// <param name="Status">步骤状态，决定颜色与删除线。</param>
public sealed record PlanStep(
    string Label,
    string? Content = null,
    string? Assignee = null,
    PlanStepStatus Status = PlanStepStatus.Pending);
