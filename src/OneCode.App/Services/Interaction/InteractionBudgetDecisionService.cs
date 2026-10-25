using OneCode.Core.Goals;

namespace OneCode.App.Services.Interaction;

/// <summary>
/// 预算决策端口的交互式实现：把预算耗尽请求交给前端通道并等待选择。
/// 无接收者时直接暂停，与后台运行语义一致。
/// </summary>
public sealed class InteractionBudgetDecisionService(IInteractionDispatcher dispatcher) : IBudgetDecisionService
{
    public async Task<GoalBudgetDecision> DecideAsync(
        BudgetExhaustedContext context,
        CancellationToken ct = default)
    {
        var request = new BudgetInteractionRequest(context);
        return await InteractionAwaiter.WaitAsync(
            dispatcher, request, request.ResponseSource, GoalBudgetDecision.Pause, ct).ConfigureAwait(false);
    }
}