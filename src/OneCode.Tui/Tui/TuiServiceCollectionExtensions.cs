using Microsoft.Extensions.DependencyInjection;
using OneCode.App.Services;
using OneCode.App.Services.BuildMode;
using OneCode.App.Services.Interaction;
using OneCode.Infrastructure.Media;

namespace OneCode.Tui;

/// <summary>
/// TUI 栈 DI 注册——Terminal.Gui 视图宿主、浮层接线与交互装配件。
/// 由组合根 <see cref="OneCode.App.OneCodeApp"/> 在交互核心注册（AddInteractionCoreServices）
/// 之后经回调串联调用；顺序依赖 ICommandRegistry / McpConfigService / IConfigManager /
/// ToolCatalog / ImagePipeline / KeybindingLoader 等核心服务的注册。
/// </summary>
public static class TuiServiceCollectionExtensions
{
    public static IServiceCollection AddTuiServices(this IServiceCollection services)
    {
        services.AddSingleton<TuiOverlayDependencies>();
        services.AddSingleton<TuiCommandSurfaceDependencies>();
        services.AddSingleton<TuiHostConfigurator>();
        services.AddSingleton(sp => new InteractiveTuiDependencies
        {
            ImagePipeline = sp.GetRequiredService<ImagePipeline>(),
            TrustService = sp.GetRequiredService<TrustService>(),
            KeybindingLoader = sp.GetRequiredService<OneCode.Infrastructure.Keybindings.KeybindingLoader>(),
            BuildRunReplay = sp.GetRequiredService<BuildRunReplayService>(),
        });
        services.AddSingleton<TuiStreamingDependencies>();
        services.AddSingleton<TuiCatalogDependencies>();
        services.AddSingleton<InteractiveSessionStack>();
        services.AddSingleton<InteractiveDiscoveryDependencies>();
        services.AddSingleton<StartupFlowCoordinator>();
        services.AddSingleton<TrustService>();
        services.AddSingleton<InteractiveBootstrapService>();
        services.AddSingleton<InteractiveKeybindingService>();
        services.AddSingleton<TuiContextFactory>();
        services.AddSingleton<InteractiveModeExecutor>();
        services.AddSingleton<InteractiveShellRunner>(sp =>
            sp.GetRequiredService<InteractiveModeExecutor>().ExecuteAsync);
        services.AddSingleton<TuiInteractionDispatcher>();
        services.ReplaceInteractionDispatcher<TuiInteractionDispatcher>();

        return services;
    }
}
