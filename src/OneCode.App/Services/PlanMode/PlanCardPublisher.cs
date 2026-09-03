using OneCode.App.Services.Streaming;
using OneCode.Core.Coordinator;
using OneCode.Core.PlanMode;

namespace OneCode.App.Services.PlanMode;

/// <summary>
/// Plan 模式领域事件发射器（事件通道统一）：原
/// <c>PlanCreated</c>/<c>WorkflowChanged</c> 专有双事件总线退役，改为将计划聚合投影变更
/// 封装为 <see cref="OrchestrationEvent.PlanProjectionChanged"/> 发布到统一领域事件总线
/// <see cref="OrchestrationEventBus"/>。TUI 宿主订阅统一总线并经
/// <c>TuiHostConfigurator.ProjectAndShowPlanAsync</c> 异步投影渲染，直订接线已删除。
/// </summary>
public sealed class PlanCardPublisher(OrchestrationEventBus bus)
{
    /// <summary>发布计划聚合投影变更（提交/审批/执行阶段推进）。</summary>
    public void Publish(PlanWorkflow workflow)
        => bus.Publish(new OrchestrationEvent.PlanProjectionChanged(workflow));
}
