using Microsoft.Extensions.DependencyInjection;

namespace OneCode.App.Services;

/// <summary>
/// 应用运行时 DI 注册——斜杠命令管线、工作模式桥与会话启动编排。
/// </summary>
public static class AppRuntimeServiceCollectionExtensions
{
    public static IServiceCollection AddAppRuntimeServices(this IServiceCollection services)
    {
        services.AddSingleton<SlashCommandPipeline>();
        services.AddSingleton<WorkingModeBridgeFactory>();
        services.AddSingleton<WorkingModeController>();
        services.AddSingleton<AppStartupService>();

        return services;
    }
}
