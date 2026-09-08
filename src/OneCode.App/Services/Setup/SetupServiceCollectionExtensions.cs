using Microsoft.Extensions.DependencyInjection;
using OneCode.App.Services.Context;
using OneCode.App.Tui;
using OneCode.Core.Config;
using OneCode.Infrastructure;
using OneCode.Infrastructure.Config;

namespace OneCode.App.Services.Setup;

/// <summary>
/// 启动/升级/信任领域 DI 注册——与启动引导实现（<see cref="StartupFlowCoordinator"/> /
/// <see cref="TrustService"/>，位于 Tui）同层维护。由组合根 <see cref="OneCode.App.OneCodeApp"/> 显式调用。
/// </summary>
public static class SetupServiceCollectionExtensions
{
    public static IServiceCollection AddSetupServices(this IServiceCollection services)
    {
        services.AddSingleton<GitInfo>();
        services.AddSingleton<ContextBuilder>();

        services.AddSingleton<StartupFlowCoordinator>();
        services.AddSingleton<ReleaseNotesService>();
        services.AddSingleton<UpgradeService>();

        services.AddSingleton<ConfigManager>(_ =>
        {
            var userConfigDir = PathsHelper.GetUserConfigDir();
            var projectConfigDir = Path.Combine(Environment.CurrentDirectory, Constants.App.ConfigDirName);
            return new ConfigManager(userConfigDir, projectConfigDir);
        });
        services.AddSingleton<IConfigManager>(sp => sp.GetRequiredService<ConfigManager>());
        services.AddSingleton<TrustService>();

        return services;
    }
}
