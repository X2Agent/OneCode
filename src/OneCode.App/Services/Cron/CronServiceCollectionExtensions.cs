using Microsoft.Extensions.DependencyInjection;
using OneCode.Automation;
using OneCode.Automation.Cron;
using OneCode.Core.Cron;

namespace OneCode.App.Services.Cron;

/// <summary>
/// Cron 领域 DI 注册——与 Cron 实现（<see cref="CronJobExecutor"/>）同目录维护；
/// 调度器宿主服务由 OneCode.Automation 的 <c>AddCronScheduler</c> 提供。
/// 由组合根 <see cref="OneCode.App.OneCodeApp"/> 显式调用。
/// </summary>
public static class CronServiceCollectionExtensions
{
    public static IServiceCollection AddCronSchedulingServices(this IServiceCollection services)
    {
        services.AddSingleton<ICronParser, CronosCronParser>();
        services.AddSingleton<CronJobExecutor>();
        services.AddSingleton<ICronJobExecutor>(sp => sp.GetRequiredService<CronJobExecutor>());
        services.AddCronScheduler();

        return services;
    }
}
