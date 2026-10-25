using OneCode.App.Services.Interaction;
using OneCode.Core.Goals;
using OneCode.Tui;

namespace OneCode.Tests;

/// <summary>
/// InteractionBudgetDecisionService——端口级契约测试（走真实 TuiInteractionDispatcher 通道）：
/// 无接收者暂停、决策回传、预算上下文透传、取消传播。
/// </summary>
public sealed class InteractionBudgetDecisionServiceTests
{
    [Fact]
    public async Task DecideAsync_NoReceiver_ReturnsPause()
    {
        var service = new InteractionBudgetDecisionService(new HeadlessInteractionDispatcher());

        var decision = await service.DecideAsync(CreateContext());

        decision.Should().Be(GoalBudgetDecision.Pause, "与后台运行「未决策即暂停保存进度」语义一致");
    }

    [Fact]
    public async Task DecideAsync_ReceiverSelects_ReturnsDecision()
    {
        BudgetInteractionRequest? delivered = null;
        var dispatcher = new TuiInteractionDispatcher();
        dispatcher.SetReceiver(request =>
        {
            delivered = (BudgetInteractionRequest)request;
            delivered.ResponseSource.TrySetResult(GoalBudgetDecision.ContinueWithExtraBudget);
        });
        var service = new InteractionBudgetDecisionService(dispatcher);
        var context = CreateContext();

        var decision = await service.DecideAsync(context);

        decision.Should().Be(GoalBudgetDecision.ContinueWithExtraBudget);
        delivered.Should().NotBeNull();
        delivered!.Context.Should().Be(context, "预算上下文必须原样透传给前端渲染");
    }

    [Fact]
    public async Task DecideAsync_CancelledWhileWaiting_PropagatesCancellation()
    {
        var dispatcher = new TuiInteractionDispatcher();
        dispatcher.SetReceiver(_ =>
        {
            // 不回传决策，等待取消
        });
        var service = new InteractionBudgetDecisionService(dispatcher);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        var act = () => service.DecideAsync(CreateContext(), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    private static BudgetExhaustedContext CreateContext() =>
        new(
            Reason: "Token 预算触顶",
            Usage: new GoalBudgetUsage(TotalAttempts: 3, TotalTokens: 200_000, Elapsed: TimeSpan.FromMinutes(40)),
            CurrentBudget: new GoalBudget(),
            StepDescription: "重构 QueryStreamEngine");
}