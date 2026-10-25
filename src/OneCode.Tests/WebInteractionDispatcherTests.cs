using OneCode.App.Services.Interaction;
using OneCode.Core.Goals;
using OneCode.WebUI.Services;

namespace OneCode.Tests;

/// <summary>
/// WebInteractionDispatcher——多电路交互路由测试：
/// 交互请求只投递给活跃电路（最近初始化或提交查询的浏览器电路），
/// 电路注销不误伤其他电路的接收者。
/// </summary>
public sealed class WebInteractionDispatcherTests
{
    private static QuestionInteractionRequest NewRequest() => new("q");

    [Fact]
    public void TrySend_NoReceiver_ReturnsFalse()
    {
        var dispatcher = new WebInteractionDispatcher();

        dispatcher.TrySend(NewRequest()).Should().BeFalse("无接收者时发送方走无头降级");
    }

    [Fact]
    public void TrySend_RegisteredButNotActive_ReturnsFalse()
    {
        var dispatcher = new WebInteractionDispatcher();
        dispatcher.Register("a", _ => { });

        // 注册不等于激活：必须由电路显式 MarkActive（初始化或提交查询时）。
        dispatcher.TrySend(NewRequest()).Should().BeFalse();
    }

    [Fact]
    public void TrySend_ActiveCircuit_ReceivesInteraction()
    {
        var dispatcher = new WebInteractionDispatcher();
        QuestionInteractionRequest? delivered = null;
        dispatcher.Register("a", request => delivered = (QuestionInteractionRequest)request);
        dispatcher.MarkActive("a");

        var sent = dispatcher.TrySend(NewRequest());

        sent.Should().BeTrue();
        delivered.Should().NotBeNull("交互请求必须送达活跃电路");
    }

    [Fact]
    public void MarkActive_SecondCircuit_RoutesInteractionsAwayFromFirst()
    {
        var dispatcher = new WebInteractionDispatcher();
        var firstDeliveries = 0;
        var secondDeliveries = 0;
        dispatcher.Register("a", _ => firstDeliveries++);
        dispatcher.Register("b", _ => secondDeliveries++);
        dispatcher.MarkActive("a");

        dispatcher.MarkActive("b");
        dispatcher.TrySend(NewRequest());

        secondDeliveries.Should().Be(1, "后提交查询的电路成为活跃电路");
        firstDeliveries.Should().Be(0, "交互请求不得双投（旧窗口不得再收到交互）");
    }

    [Fact]
    public void Unregister_ActiveCircuit_HandsOverToSoleRemainingCircuit()
    {
        var dispatcher = new WebInteractionDispatcher();
        QuestionInteractionRequest? delivered = null;
        dispatcher.Register("a", _ => { });
        dispatcher.Register("b", request => delivered = (QuestionInteractionRequest)request);
        dispatcher.MarkActive("a");

        dispatcher.Unregister("a");
        var sent = dispatcher.TrySend(NewRequest());

        sent.Should().BeTrue("活跃电路离线且仅剩一个电路时无缝交还");
        delivered.Should().NotBeNull("交还后的电路必须能收到交互请求");
    }

    [Fact]
    public void Unregister_NonActiveCircuit_KeepsActiveRouting()
    {
        var dispatcher = new WebInteractionDispatcher();
        var activeDeliveries = 0;
        dispatcher.Register("a", _ => activeDeliveries++);
        dispatcher.Register("b", _ => { });
        dispatcher.MarkActive("a");

        dispatcher.Unregister("b");
        dispatcher.TrySend(NewRequest());

        activeDeliveries.Should().Be(1, "非活跃电路注销不得影响活跃电路的路由");
    }

    [Fact]
    public void Unregister_ActiveCircuit_MultipleRemaining_AwaitsExplicitMarkActive()
    {
        var dispatcher = new WebInteractionDispatcher();
        dispatcher.Register("a", _ => { });
        dispatcher.Register("b", _ => { });
        dispatcher.Register("c", _ => { });
        dispatcher.MarkActive("a");

        dispatcher.Unregister("a");

        // 剩余电路多于一个时无从判定用户在哪个窗口，等待 MarkActive 再路由。
        dispatcher.TrySend(NewRequest()).Should().BeFalse("活跃电路离线后不得随机挑一个电路投递");
    }

    [Fact]
    public void TrySend_AfterBudgetDecisionRouting_StillSingleDelivery()
    {
        // 预算决策等交互与提问同走一条通道：活跃电路唯一性对全部 InteractionRequest 生效。
        var dispatcher = new WebInteractionDispatcher();
        var budgetDeliveries = 0;
        dispatcher.Register("a", _ => budgetDeliveries++);
        dispatcher.Register("b", _ => budgetDeliveries++);
        dispatcher.MarkActive("b");

        dispatcher.TrySend(new BudgetInteractionRequest(new BudgetExhaustedContext(
            "reason",
            new GoalBudgetUsage(0, 1, null),
            new GoalBudget { MaxSubGoalAttempts = 0, MaxTotalTokens = 1 })));

        budgetDeliveries.Should().Be(1);
    }
}
