using Microsoft.Extensions.DependencyInjection;

namespace OneCode.App.Services.PlanMode;

/// <summary>
/// PlanMode 领域 DI 注册——与 Plan 实现（<see cref="PlanModeService"/> /
/// <see cref="PlanCardPublisher"/>）同目录维护。由组合根 <see cref="OneCode.App.OneCodeApp"/> 显式调用。
/// </summary>
public static class PlanModeServiceCollectionExtensions
{
    public static IServiceCollection AddPlanModeServices(this IServiceCollection services)
    {
        services.AddSingleton<PlanModeService>();
        services.AddSingleton<IPlanModeService>(sp => sp.GetRequiredService<PlanModeService>());

        services.AddSingleton<PlanCardPublisher>();

        return services;
    }
}
