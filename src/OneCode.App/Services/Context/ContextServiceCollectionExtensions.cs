using Microsoft.Extensions.DependencyInjection;
using OneCode.App.Services.PlanMode;

namespace OneCode.App.Services.Context;

/// <summary>
/// Context（上下文装配）领域 DI 注册——与上下文提供方（<see cref="TaskContextProvider"/>）
/// 同目录维护。由组合根 <see cref="OneCode.App.OneCodeApp"/> 显式调用。
/// </summary>
public static class ContextServiceCollectionExtensions
{
    public static IServiceCollection AddContextServices(this IServiceCollection services)
    {
        services.AddSingleton<TaskContextProvider>();

        services.AddSingleton<PlanExecutionContextProvider>(sp =>
            new PlanExecutionContextProvider(
                sp.GetRequiredService<IPlanWorkflowApplicationService>(),
                sp.GetRequiredService<IPermissionModeProvider>()));

        return services;
    }
}
