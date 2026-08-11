using Microsoft.Extensions.DependencyInjection;
using OneCode.App.Services;
using OneCode.App.Services.BuildMode;
using OneCode.App.Services.Streaming;
using OneCode.App.Tui;
using OneCode.Core.Cost;
using OneCode.Infrastructure.Media;

namespace OneCode.App;

public static partial class ServiceCollectionExtensions
{
    public static IServiceCollection RegisterInteractiveServices(this IServiceCollection services)
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
            CostTracker = sp.GetRequiredService<ICostTracker>(),
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
