using OneCode.Core.Goals;

namespace OneCode.App.Services.Agent;

/// <summary>
/// <see cref="MainAgentRunner"/> 的预算耗尽决策。
/// 交互模式经 <see cref="IBudgetDecisionService"/> 端口询问用户，与 Goal 工作流共用同一交互通道；
/// 决策语义由 BudgetGuard 中间件消费：ContinueWithExtraBudget=预算加倍放行本轮、
/// WrapUpAndStop=注入收尾总结轮次后正常结束、其余=硬停短路。
/// </summary>
public partial class MainAgentRunner
{
    /// <summary>
    /// 预算 100% 消耗时的用户询问：构造单次 Agent 运行场景（无子目标语义）的预算上下文，
    /// 交由 <see cref="IBudgetDecisionService"/> 取得决策。前端按 AgentRun 场景渲染
    /// 「追加预算继续 / 收尾总结后结束 / 直接停止」选项集。
    /// </summary>
    private async Task<GoalBudgetDecision> RequestBudgetDecisionAsync(
        long usedTokens,
        long limitTokens,
        CancellationToken ct)
    {
        var context = new BudgetExhaustedContext(
            Reason: $"可计费 token 已消耗 {usedTokens:N0}，达到本次执行上限 {limitTokens:N0}",
            // 单次 Agent 运行只有 token 维度预算：尝试轮次与墙钟不参与限制与展示。
            Usage: new GoalBudgetUsage(TotalAttempts: 0, TotalTokens: usedTokens, Elapsed: null),
            CurrentBudget: new GoalBudget
            {
                MaxSubGoalAttempts = 0,
                MaxTotalTokens = limitTokens,
                MaxWallClock = null,
            },
            Scope: BudgetPromptScope.AgentRun);

        return await _budgetDecisionService.DecideAsync(context, ct).ConfigureAwait(false);
    }
}
