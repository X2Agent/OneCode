namespace OneCode.Core.Goals;

/// <summary>
/// 预算耗尽时的用户决策端口。App 提供统一默认实现（经 <c>IInteractionDispatcher</c>
/// 投递到前端），交互式前端只替换投递通道。
/// </summary>
/// <remarks>
/// 无投递通道可用时（无头宿主 / 后台运行），默认实现直接返回
/// <see cref="GoalBudgetDecision.Pause"/>，与「未开启预算提示时直接暂停」的既有语义一致。
/// </remarks>
public interface IBudgetDecisionService
{
    /// <summary>
    /// 请求用户决定预算耗尽后的下一步动作。
    /// </summary>
    /// <param name="context">预算上下文，供界面渲染。</param>
    /// <param name="ct">取消令牌；取消时必须移除等待项，不得悬挂。</param>
    /// <returns>用户的决策。</returns>
    Task<GoalBudgetDecision> DecideAsync(
        BudgetExhaustedContext context,
        CancellationToken ct = default);
}
