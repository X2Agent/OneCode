using Microsoft.Extensions.DependencyInjection;

namespace OneCode.App.Services.AutoDream;

/// <summary>
/// AutoDream 领域 DI 注册——与后台记忆整合实现（<see cref="AutoDreamService"/>）同目录维护。
/// 由组合根 <see cref="OneCode.App.OneCodeApp"/> 显式调用。
/// </summary>
public static class AutoDreamServiceCollectionExtensions
{
    public static IServiceCollection AddAutoDreamServices(this IServiceCollection services)
    {
        // AutoDream: 后台记忆整合服务。注册为 Singleton + HostedService：
        // - Singleton：供 /memory autodream trigger 命令通过 DI 获取并调用 Trigger()
        // - HostedService：让 BackgroundService.ExecuteAsync 随宿主生命周期自动启停（1h 轮询是唯一自动触发路径）
        services.AddSingleton<AutoDreamAgentDependencies>();
        services.AddSingleton<AutoDreamStorageDependencies>();
        services.AddSingleton<AutoDreamService>();
        services.AddHostedService(sp => sp.GetRequiredService<AutoDreamService>());

        return services;
    }
}
