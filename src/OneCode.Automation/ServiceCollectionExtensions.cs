using Microsoft.Extensions.DependencyInjection;
using OneCode.Automation.Cron;
using OneCode.Automation.ModelCatalog;
using OneCode.Automation.Yolo;
using OneCode.Core.Tools;

namespace OneCode.Automation;

/// <summary>
/// DI registration extensions for <c>OneCode.Automation</c>. Exposes focused Add* methods so
/// the App composition root can wire each automation subsystem independently (some depend on
/// App-supplied abstractions like <see cref="ICronJobExecutor"/> and must be registered after
/// the App-side implementation).
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Register <see cref="CronSchedulerService"/> as both a singleton and a hosted service
    /// (double-register so the rest of the app can resolve the singleton while the hosted
    /// lifecycle wraps the same instance). Caller must register an <see cref="ICronJobExecutor"/>
    /// implementation separately.
    /// </summary>
    public static IServiceCollection AddCronScheduler(this IServiceCollection services)
    {
        services.AddSingleton<CronSchedulerService>();
        services.AddHostedService(sp => sp.GetRequiredService<CronSchedulerService>());
        return services;
    }

    /// <summary>
    /// Register the unified cron tool POCO and Catalog metadata
    /// (via the explicit tool registration API).
    /// One call completes both DI registration and Catalog metadata registration.
    /// </summary>
    public static IServiceCollection AddCronTools(this IServiceCollection services)
    {
        // Deferred 层：cron 工具低频但高风险，不自动加载，仅通过 ToolSearch 显式激活
        services.AddToolInstance("Cron", (CronTool tool) =>
                Microsoft.Extensions.AI.AIFunctionFactory.Create(tool.ExecuteAsync, name: "Cron"),
            ToolRisk.Safe,
            searchHint: "manage scheduled cron jobs (create/list/delete/pause/resume)",
            loadPolicy: ToolLoadPolicy.Deferred, keywords: ["cron", "schedule"]);
        return services;
    }

    /// <summary>
    /// Register <see cref="ModelCatalogRefreshService"/> as a singleton + hosted service.
    /// Depends only on <see cref="OneCode.Core.Models.IModelCatalogCache"/> (Core).
    /// </summary>
    public static IServiceCollection AddModelCatalogRefresh(this IServiceCollection services)
    {
        services.AddSingleton<ModelCatalogRefreshService>();
        services.AddHostedService(sp => sp.GetRequiredService<ModelCatalogRefreshService>());
        return services;
    }

    /// <summary>
    /// Register <see cref="YoloRuleStoreLoader"/> as a hosted service.
    /// Requires <see cref="OneCode.Core.Permissions.Yolo.YoloRuleStore"/> and
    /// <see cref="OneCode.Core.Permissions.Yolo.IYoloRuleFileStore"/> registered by the App composition root.
    /// </summary>
    public static IServiceCollection AddYoloRuleStoreLoader(this IServiceCollection services)
    {
        services.AddHostedService<YoloRuleStoreLoader>();
        return services;
    }
}
