using Microsoft.Extensions.DependencyInjection;
using OneCode.App.Services.Hooks.Notifications;
using OneCode.Core.Hooks.Notifications;

namespace OneCode.App.Services.Hooks;

/// <summary>
/// Hooks 领域 DI 注册——与 Hook 实现（<see cref="HookRegistry"/> / <see cref="HookExecutionService"/>）
/// 同目录维护。由组合根 <see cref="OneCode.App.OneCodeApp"/> 显式调用。
/// </summary>
public static class HookServiceCollectionExtensions
{
    public static IServiceCollection AddHookServices(this IServiceCollection services)
    {
        services.AddSingleton<GlobHookMatcher>();
        services.AddSingleton<HookLoadDiagnostics>();
        services.AddSingleton<HookSettingsLoader>();
        services.AddSingleton<HookRegistry>();
        services.AddSingleton<HookPolicyService>();

        // HookExecutionService 经 IEnumerable<IHookExecutor> 注入并按 Type 分发——
        // 新增执行器只需在此追加一行（无需 keyed 服务）。
        services.AddSingleton<IHookExecutor, CommandHookExecutor>();
        services.AddSingleton<IHookExecutor, NotificationHookExecutor>();
        services.AddSingleton<IHookExecutor, HttpHookExecutor>();

        services.AddSingleton<INotificationProvider, FeishuNotificationProvider>();
        services.AddSingleton<INotificationProvider, WeChatWorkNotificationProvider>();

        services.AddHttpClient<FeishuNotificationProvider>();
        services.AddHttpClient<WeChatWorkNotificationProvider>();

        // 声明式通知渠道（notification-providers.json）：定义优先、编译型兜底
        services.AddHttpClient(NotificationProviderRegistry.HttpClientName);
        services.AddSingleton<NotificationProviderDefinitionLoader>();
        services.AddSingleton<NotificationProviderRegistry>();

        // 宿主停止时兜底关闭前台会话（reason=other；不再触发 hook）
        services.AddHostedService<HostStopSessionCloseService>();

        services.AddSingleton<HookExecutionService>();
        services.AddSingleton<IHookExecutionService>(sp => sp.GetRequiredService<HookExecutionService>());
        services.AddSingleton<HookConfigBootstrapper>();
        services.AddSingleton<HookConfigHotReloader>();

        return services;
    }
}
