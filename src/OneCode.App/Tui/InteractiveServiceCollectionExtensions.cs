using Microsoft.Extensions.DependencyInjection;
using OneCode.App.Services;
using OneCode.App.Services.BuildMode;
using OneCode.App.Services.Streaming;
using OneCode.Infrastructure.Media;

namespace OneCode.App.Tui;

/// <summary>
/// TUI 交互领域 DI 注册——与交互实现（<see cref="InteractiveModeExecutor"/> /
/// <see cref="TuiHostConfigurator"/>）同目录维护。由组合根 <see cref="OneCode.App.OneCodeApp"/> 显式调用。
/// </summary>
public static class InteractiveServiceCollectionExtensions
{
    public static IServiceCollection AddInteractiveServices(this IServiceCollection services)
    {
        services.AddSingleton<PromptConfigBuilder>();
        services.AddSingleton<PromptRuntimeDependencies>();
        services.AddSingleton<ThinkingParamsResolver>();
        services.AddSingleton<TuiOverlayDependencies>();
        services.AddSingleton<TuiCommandSurfaceDependencies>();
        services.AddSingleton<TuiHostConfigurator>();
        services.AddSingleton<BuildRunTuiReplayService>();
        services.AddSingleton<SlashCommandPipeline>();
        services.AddSingleton<OrchestrationStreamService>();
        services.AddSingleton<QueryOrchestrationDependencies>();
        services.AddSingleton<QueryRuntimeDependencies>();
        services.AddSingleton<QueryStreamService>();
        services.AddSingleton(sp => new InteractiveTuiDependencies
        {
            ImagePipeline = sp.GetRequiredService<ImagePipeline>(),
            TrustService = sp.GetRequiredService<TrustService>(),
            KeybindingLoader = sp.GetRequiredService<OneCode.Infrastructure.Keybindings.KeybindingLoader>(),
            BuildRunTuiReplay = sp.GetRequiredService<BuildRunTuiReplayService>(),
        });
        services.AddSingleton<TuiStreamingDependencies>();
        services.AddSingleton<TuiCatalogDependencies>();
        services.AddSingleton<WorkingModeBridgeFactory>();
        services.AddSingleton<WorkingModeController>();
        services.AddSingleton<InteractiveSessionStack>();
        services.AddSingleton<InteractiveDiscoveryDependencies>();
        services.AddSingleton<InteractiveBootstrapService>();
        services.AddSingleton<InteractiveKeybindingService>();
        services.AddSingleton<TuiContextFactory>();
        services.AddSingleton<InteractiveModeExecutor>();
        services.AddSingleton<AppStartupService>();

        return services;
    }
}
