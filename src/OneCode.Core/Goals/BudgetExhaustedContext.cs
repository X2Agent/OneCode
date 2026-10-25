namespace OneCode.Core.Goals;

/// <summary>
/// 预算耗尽请求的只读上下文，供前端渲染决策卡片。
/// </summary>
/// <param name="Reason">触发原因（尝试轮次、Token 或墙钟触顶）。</param>
/// <param name="Usage">当前预算用量。</param>
/// <param name="CurrentBudget">当前生效的预算上限。</param>
/// <param name="StepDescription">当前子目标描述；无进行中子目标时为 null。</param>
/// <param name="Scope">提示场景：决定前端展示的决策选项集（Goal 子目标工作流或单次 Agent 运行）。</param>
public sealed record BudgetExhaustedContext(
    string Reason,
    GoalBudgetUsage Usage,
    GoalBudget CurrentBudget,
    string? StepDescription = null,
    BudgetPromptScope Scope = BudgetPromptScope.GoalRun);

/// <summary>
/// 预算耗尽提示的场景。前端据此选择决策选项集。
/// </summary>
public enum BudgetPromptScope
{
    /// <summary>Goal 子目标工作流：可追加预算、跳过当前子目标、暂停保存或终止。</summary>
    GoalRun,

    /// <summary>单次 Agent 运行的 token 预算：可追加预算、收尾总结后结束或直接停止，无子目标语义。</summary>
    AgentRun,
}
