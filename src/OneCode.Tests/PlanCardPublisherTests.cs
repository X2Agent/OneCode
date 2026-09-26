using OneCode.App.Services.PlanMode;
using OneCode.App.Services.Streaming;
using OneCode.Core.Coordinator;
using OneCode.Core.Domain;
using OneCode.Core.PlanMode;

namespace OneCode.Tests;

/// <summary>
/// 事件通道统一：<see cref="PlanCardPublisher"/> 已收敛为统一领域
/// 事件总线（<see cref="OrchestrationEventBus"/>）的 Plan 侧发射器——验证其发布的
/// <see cref="OrchestrationEvent.PlanProjectionChanged"/> 载荷与多路订阅广播语义。
/// </summary>
public sealed class PlanCardPublisherTests
{
    [Fact]
    public void Publish_WithSubscriber_EmitsPlanProjectionChangedWithExactWorkflow()
    {
        var bus = new OrchestrationEventBus();
        var sut = new PlanCardPublisher(bus);
        var workflow = PlanWorkflow.Create(SessionId.NewId());
        OrchestrationEvent.PlanProjectionChanged? received = null;
        bus.Subscribe(evt => received = evt as OrchestrationEvent.PlanProjectionChanged);

        sut.Publish(workflow);

        received.Should().NotBeNull();
        received!.Workflow.Should().BeSameAs(workflow);
    }

    [Fact]
    public void Publish_WithoutSubscribers_DoesNotThrow()
    {
        // Headless/Cron 场景无 TUI 订阅者——总线 Publish 必须为 no-op，
        // CreatePlanTool 在无 TUI 的运行中调用 Publish 不能崩溃。
        var bus = new OrchestrationEventBus();
        var sut = new PlanCardPublisher(bus);

        var act = () => sut.Publish(PlanWorkflow.Create(SessionId.NewId()));

        act.Should().NotThrow();

        // 无订阅者发布后总线不得进入损坏状态：后续订阅者仍能正常收到事件。
        PlanWorkflow? lateSubscriber = null;
        bus.Subscribe(evt => lateSubscriber = ((OrchestrationEvent.PlanProjectionChanged)evt).Workflow);
        var workflow = PlanWorkflow.Create(SessionId.NewId());
        sut.Publish(workflow);
        lateSubscriber.Should().BeSameAs(workflow, "无订阅者发布是 no-op，不得影响后续订阅生效");
    }

    [Fact]
    public void Publish_WithMultipleSubscribers_NotifiesAllWithSameWorkflow()
    {
        var bus = new OrchestrationEventBus();
        var sut = new PlanCardPublisher(bus);
        var workflow = PlanWorkflow.Create(SessionId.NewId());
        var received = new List<PlanWorkflow>();
        bus.Subscribe(evt => received.Add(((OrchestrationEvent.PlanProjectionChanged)evt).Workflow));
        bus.Subscribe(evt => received.Add(((OrchestrationEvent.PlanProjectionChanged)evt).Workflow));

        sut.Publish(workflow);

        received.Should().HaveCount(2);
        received[0].Should().BeSameAs(workflow);
        received[1].Should().BeSameAs(workflow);
    }

    [Fact]
    public void Subscribe_Unsubscribe_StopsReceivingEvents()
    {
        var bus = new OrchestrationEventBus();
        var sut = new PlanCardPublisher(bus);
        var received = new List<PlanWorkflow>();
        using var subscription = bus.Subscribe(
            evt => received.Add(((OrchestrationEvent.PlanProjectionChanged)evt).Workflow));

        subscription.Dispose();
        sut.Publish(PlanWorkflow.Create(SessionId.NewId()));

        received.Should().BeEmpty();
    }

    [Fact]
    public void Bus_HasSubscribers_ReflectsActiveSubscriptions()
    {
        // headless 判据随事件通道统一迁移到总线：无订阅者时调用方不应阻塞等待用户决策。
        var bus = new OrchestrationEventBus();
        bus.HasSubscribers.Should().BeFalse();
        using var subscription = bus.Subscribe(_ => { });
        bus.HasSubscribers.Should().BeTrue();
        subscription.Dispose();
        bus.HasSubscribers.Should().BeFalse();
    }
}
