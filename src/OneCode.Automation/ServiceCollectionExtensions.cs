using Microsoft.Extensions.DependencyInjection;
using OneCode.Automation.Cron;
using OneCode.Automation.ModelCatalog;
using OneCode.Automation.Yolo;
using OneCode.Core.Cron;
using OneCode.Core.Models;
using OneCode.Core.Permissions.Yolo;
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
    /// Register cron tool POCOs (Create/List/Delete/Pause/Resume) with custom DI factories
    /// AND Catalog metadata (via <see cref="ToolServiceCollectionExtensions.AddTool{T}"/>).
    /// One call per tool completes both DI registration and Catalog metadata registration,
    /// eliminating the previous two-place maintenance (DI here + Catalog in ToolCatalog).
    /// </summary>
    public static IServiceCollection AddCronTools(this IServiceCollection services)
    {
        // 自定义 DI 工厂（CronCreateTool 依赖 ICronParser + CronSchedulerService）
        // GetRequiredService: AddCronTools 必须与 AddCronScheduler 配合使用，
        // 未注册调度器时 fail-fast 而非静默返回 null。
        services.AddSingleton<CronCreateTool>(sp => new CronCreateTool(
            sp.GetRequiredService<ICronParser>(),
            sp.GetRequiredService<CronSchedulerService>()));
        services.AddSingleton<CronDeleteTool>(sp => new CronDeleteTool(
            sp.GetRequiredService<CronSchedulerService>()));
        services.AddSingleton<CronListTool>(sp => new CronListTool(
            sp.GetRequiredService<CronSchedulerService>()));
        services.AddSingleton<CronPauseTool>(sp => new CronPauseTool(
            sp.GetRequiredService<CronSchedulerService>()));
        services.AddSingleton<CronResumeTool>(sp => new CronResumeTool(
            sp.GetRequiredService<CronSchedulerService>()));

        // Catalog 元数据注册（TryAddSingleton 不会覆盖上面的自定义工厂）
        // Deferred 层：cron 工具低频但高风险，不自动加载，仅通过 ToolSearch 显式激活
        services.AddTool<CronCreateTool>("CronCreate", nameof(CronCreateTool.CreateAsync), ToolRisk.Safe, searchHint: "create a cron job",
            loadPolicy: ToolLoadPolicy.Deferred, keywords: ["cron", "schedule"]);
        services.AddTool<CronListTool>("CronList", nameof(CronListTool.ListAsync), ToolRisk.ReadOnly, searchHint: "list cron jobs",
            loadPolicy: ToolLoadPolicy.Deferred, keywords: ["cron", "schedule"]);
        services.AddTool<CronDeleteTool>("CronDelete", nameof(CronDeleteTool.Delete), ToolRisk.Destructive, concurrency: false, searchHint: "delete a cron job",
            loadPolicy: ToolLoadPolicy.Deferred, keywords: ["cron", "schedule"]);
        services.AddTool<CronPauseTool>("CronPause", nameof(CronPauseTool.PauseAsync), ToolRisk.Safe, concurrency: false, searchHint: "pause a cron job",
            loadPolicy: ToolLoadPolicy.Deferred, keywords: ["cron", "schedule"]);
        services.AddTool<CronResumeTool>("CronResume", nameof(CronResumeTool.ResumeAsync), ToolRisk.Safe, concurrency: false, searchHint: "resume a paused cron job",
            loadPolicy: ToolLoadPolicy.Deferred, keywords: ["cron", "schedule"]);
        return services;
    }

    /// <summary>
    /// Register <see cref="ModelCatalogRefreshService"/> as a singleton + hosted service.
    /// Depends only on <see cref="IModelCatalogCache"/> (Core).
    /// </summary>
    public static IServiceCollection AddModelCatalogRefresh(this IServiceCollection services)
    {
        services.AddSingleton<ModelCatalogRefreshService>();
        services.AddHostedService(sp => sp.GetRequiredService<ModelCatalogRefreshService>());
        return services;
    }

    /// <summary>
    /// Register <see cref="YoloRuleStoreLoader"/> as a hosted service.
    /// Requires <see cref="YoloRuleStore"/> and <see cref="IYoloRuleFileStore"/> registered by the App composition root.
    /// </summary>
    public static IServiceCollection AddYoloRuleStoreLoader(this IServiceCollection services)
    {
        services.AddHostedService<YoloRuleStoreLoader>();
        return services;
    }
}
