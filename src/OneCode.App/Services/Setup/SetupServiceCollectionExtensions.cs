using Microsoft.Extensions.DependencyInjection;
using OneCode.App.Services.Context;
using OneCode.Core.Config;
using OneCode.Infrastructure;
using OneCode.Infrastructure.Config;

namespace OneCode.App.Services.Setup;

/// <summary>
/// 启动/升级领域 DI 注册——启动引导数据、发布说明与自升级。
/// 信任门（TrustService）与启动流程协调（StartupFlowCoordinator）随 TUI 栈
/// 在 OneCode.Tui 的 AddTuiServices 注册。由组合根 <see cref="OneCode.App.OneCodeApp"/> 显式调用。
/// </summary>
public static class SetupServiceCollectionExtensions
{
    public static IServiceCollection AddSetupServices(this IServiceCollection services)
    {
        services.AddSingleton<GitInfo>();
        services.AddSingleton<ContextBuilder>();

        services.AddSingleton<ReleaseNotesService>();
        services.AddSingleton<UpgradeService>();

        services.AddSingleton<ConfigManager>(sp =>
        {
            var userConfigDir = PathsHelper.GetUserConfigDir();
            var projectConfigDir = Path.Combine(Environment.CurrentDirectory, Constants.App.ConfigDirName);
            return new ConfigManager(
                userConfigDir,
                projectConfigDir,
                sp.GetService<ILogger<ConfigManager>>());
        });
        services.AddSingleton<IConfigManager>(sp => sp.GetRequiredService<ConfigManager>());

        return services;
    }
}
