namespace OneCode.Core.Goals;

/// <summary>
/// 预算耗尽时的用户决策（Goal 子目标工作流与单次 Agent 运行共用同一端口）。
/// 各取值仅在对应场景有效（由 <see cref="BudgetExhaustedContext.Scope"/> 区分）。
/// </summary>
public enum GoalBudgetDecision
{
    /// <summary>继续执行并自动追加预算额度。</summary>
    ContinueWithExtraBudget,

    /// <summary>跳过当前子目标，继续执行后续子目标（仅 Goal 场景）。</summary>
    SkipCurrentStep,

    /// <summary>暂停保存当前进度（稍后通过 /resume 恢复）。</summary>
    Pause,

    /// <summary>终止当前执行。</summary>
    Abort,

    /// <summary>收尾总结后结束（仅单次 Agent 运行场景）：注入收尾总结轮次后正常结束。</summary>
    WrapUpAndStop,
}
