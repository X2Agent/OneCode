using OneCode.Core.Coordinator;

namespace OneCode.App.Services.Streaming;

/// <summary>
/// 统一领域事件总线（事件通道统一）：<see cref="OrchestrationEvent"/>
/// （Core/Coordinator）是跨模式唯一的领域事件信封，本类型是其 App 层承载——
/// 模式侧发射器（<c>PlanCardPublisher</c> 等）调用 <see cref="Publish"/>，
/// TUI 宿主等长生命周期订阅者经 <see cref="Subscribe"/> 接收。
/// Team/Goal 的 run 级事件流仍走 per-operation Channel（OrchestrationStreamService），
/// 本总线服务跨 run 的投影事件（计划卡片等）。
/// </summary>
/// <remarks>
/// 生命周期与应用一致（singleton）。Publish 在后台线程同步触发订阅者，
/// 订阅者自行负责 UI 线程封送（与原 PlanCardPublisher 约定一致）；
/// 无订阅者时 Publish 为 no-op（headless/Cron 安全）。
/// </remarks>
public sealed class OrchestrationEventBus
{
    private event Action<OrchestrationEvent>? Received;

    /// <summary>是否存在订阅者（TUI 已接线）。Headless/Cron 场景下无订阅者。</summary>
    public bool HasSubscribers => Received is not null;

    /// <summary>发布领域事件。</summary>
    public void Publish(OrchestrationEvent evt) => Received?.Invoke(evt);

    /// <summary>订阅领域事件；返回的 <see cref="IDisposable"/> 用于退订。</summary>
    public IDisposable Subscribe(Action<OrchestrationEvent> handler)
    {
        Received += handler;
        return new Subscription(this, handler);
    }

    private sealed class Subscription(OrchestrationEventBus bus, Action<OrchestrationEvent> handler) : IDisposable
    {
        public void Dispose() => bus.Received -= handler;
    }
}