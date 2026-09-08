using Microsoft.Extensions.DependencyInjection;
using OneCode.App.Services;
using OneCode.Core.Build;
using OneCode.Core.Prompt;
using OneCode.Infrastructure.Build;

namespace OneCode.App.Services.BuildMode;

/// <summary>
/// BuildMode 领域 DI 注册——与 Build 实现（<see cref="BuildRunCoordinator"/> /
/// <see cref="BuildTaskLinker"/>）同目录维护。由组合根 <see cref="OneCode.App.OneCodeApp"/> 显式调用。
/// </summary>
public static class BuildModeServiceCollectionExtensions
{
    public static IServiceCollection AddBuildModeServices(this IServiceCollection services)
    {
        services.AddSingleton<JsonBuildRunStore>();
        services.AddSingleton<IBuildRunStore>(sp => sp.GetRequiredService<JsonBuildRunStore>());
        services.AddSingleton<IBuildRunEventStore>(sp => sp.GetRequiredService<JsonBuildRunStore>());
        services.AddSingleton<IWorkspaceFingerprintProvider, WorkspaceFingerprintProvider>();
        services.AddSingleton<RequirementAssessmentService>();
        services.AddSingleton<BuildStateTransitionService>();
        services.AddSingleton<BuildTaskLinker>();
        services.AddSingleton<IBuildRunCoordinator, BuildRunCoordinator>();

        services.AddSingleton<BuildModeAttachmentProvider>(sp =>
            new BuildModeAttachmentProvider(
                sp.GetRequiredService<IPermissionModeProvider>(),
                sp.GetRequiredService<IPromptManager>()));

        services.AddSingleton<Services.BuildMode.ControlledBuildAttemptWorkflowCompiler>();
        services.AddSingleton<Services.BuildMode.ControlledBuildAttemptHost>();

        return services;
    }
}
