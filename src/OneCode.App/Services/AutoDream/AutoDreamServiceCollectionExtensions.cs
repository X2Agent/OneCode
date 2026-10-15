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
        // AutoDream: 任务级后台记忆整合服务。注册为 Singleton + IAutoDreamService + HostedService：
        // - Singleton / IAutoDreamService：供 QueryStreamEngine.FinalizeAsync 与 /memory autodream trigger 调用
        // - HostedService：让 BackgroundService.ExecuteAsync 随宿主生命周期启停，异步消费任务完成信号
        services.AddSingleton<AutoDreamAgentDependencies>();
        services.AddSingleton<AutoDreamStorageDependencies>();
        services.AddSingleton<AutoDreamService>();
        services.AddSingleton<IAutoDreamService>(sp => sp.GetRequiredService<AutoDreamService>());
        services.AddHostedService(sp => sp.GetRequiredService<AutoDreamService>());

        return services;
    }
}
