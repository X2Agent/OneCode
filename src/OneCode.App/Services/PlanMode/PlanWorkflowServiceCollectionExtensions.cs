using Microsoft.Extensions.DependencyInjection;

namespace OneCode.App.Services.PlanMode;

/// <summary>
/// Plan 工作流领域 DI 注册——与 Plan 执行实现（<see cref="PlanWorkflowApplicationService"/> /
/// <see cref="PlanExecutionRecoveryService"/>）同目录维护。由组合根 <see cref="OneCode.App.OneCodeApp"/> 显式调用。
/// </summary>
public static class PlanWorkflowServiceCollectionExtensions
{
    public static IServiceCollection AddPlanWorkflowServices(this IServiceCollection services)
    {
        services.AddSingleton<IPlanAggregateStore, PlanAggregateStore>();
        services.AddSingleton<IPlanWorkflowApplicationService, PlanWorkflowApplicationService>();
        services.AddSingleton<IPlanAgentRunDispatcher, PlanAgentRunDispatcher>();
        services.AddSingleton<AggregateApprovalGate>();
        services.AddSingleton<PlanExecutionRecoveryService>();
        services.AddHostedService(sp => sp.GetRequiredService<PlanExecutionRecoveryService>());

        return services;
    }
}
