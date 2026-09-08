using InfraConstants = OneCode.Infrastructure.Config.Constants;
using Microsoft.Extensions.DependencyInjection;
using OneCode.Core.Prompt;
using OneCode.Infrastructure;
using OneCode.Infrastructure.Prompt;

namespace OneCode.App.Services;

/// <summary>
/// Prompt 领域 DI 注册——三层覆盖（项目/用户/内置）在此组装，
/// 缺失目录的处理策略见 src/OneCode.App/AGENTS.md。由组合根 <see cref="OneCodeApp"/> 显式调用。
/// </summary>
public static class PromptServiceCollectionExtensions
{
    public static IServiceCollection AddPromptServices(
        this IServiceCollection services,
        string workingDir)
    {
        services.AddSingleton<Core.Prompt.PromptManager>(sp =>
        {
            var manager = new Core.Prompt.PromptManager(
                sp.GetService<ILogger<Core.Prompt.PromptManager>>());

            var projectPromptsDir = Path.Combine(workingDir, InfraConstants.App.ConfigDirName, InfraConstants.Subdirs.Prompts);
            if (Directory.Exists(projectPromptsDir))
                manager.AddStore(new Infrastructure.Prompt.FilePromptStore(projectPromptsDir));

            var userPromptsDir = Path.Combine(PathsHelper.GetUserConfigDir(), InfraConstants.Subdirs.Prompts);
            if (Directory.Exists(userPromptsDir))
                manager.AddStore(new Infrastructure.Prompt.FilePromptStore(userPromptsDir));

            var defaultPromptsDir = Path.Combine(AppContext.BaseDirectory, InfraConstants.Subdirs.Prompts);
            manager.AddStore(new Infrastructure.Prompt.FilePromptStore(defaultPromptsDir));

            return manager;
        });
        services.AddSingleton<Core.Prompt.IPromptManager>(sp => sp.GetRequiredService<Core.Prompt.PromptManager>());
        services.AddSingleton<PromptComposer>();

        return services;
    }
}
