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
    /// <param name="services">DI 容器。</param>
    /// <param name="workingDir">进程工作目录；durable 作业按此归属过滤，只在归属项目的进程中触发。</param>
    public static IServiceCollection AddCronSchedulingServices(this IServiceCollection services, string workingDir)
    {
        services.AddSingleton<ICronParser, CronosCronParser>();
        services.AddSingleton<CronJobExecutor>();
        services.AddSingleton<ICronJobExecutor>(sp => sp.GetRequiredService<CronJobExecutor>());
        services.AddCronScheduler(projectDir: workingDir);

        return services;
    }
}
