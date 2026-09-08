using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using OneCode.App.Services.Agent;
using OneCode.Core.Goals;
using OneCode.Core.Prompt;
using OneCode.Infrastructure.Goals;

namespace OneCode.App.Services.GoalMode;

/// <summary>
/// GoalMode 领域 DI 注册——与 Goal 实现（<see cref="GoalRunApplicationService"/> /
/// <see cref="GoalCompletionService"/>）同目录维护。由组合根 <see cref="OneCode.App.OneCodeApp"/> 显式调用。
/// </summary>
public static class GoalServiceCollectionExtensions
{
    public static IServiceCollection AddGoalServices(this IServiceCollection services)
    {
        services.AddSingleton<GoalContextState>();
        services.AddSingleton<IGoalRunStore, JsonGoalRunStore>();
        services.AddSingleton<IGoalWorkspaceService, GitGoalWorkspaceService>();
        services.AddSingleton<IGoalRunApplicationService, GoalRunApplicationService>();
        services.AddSingleton<GoalWorkflowCompiler>();
        services.AddSingleton<GoalWorkflowHost>();

        services.AddSingleton<GoalDecomposer>();
        services.AddSingleton<IGoalPlanningService>(sp => sp.GetRequiredService<GoalDecomposer>());
        services.AddSingleton(sp => new GoalSubGoalExecutor(
            sp.GetRequiredService<IMainAgentRunner>(),
            sp.GetRequiredService<IChatClient>(),
            sp.GetRequiredService<ILoggerFactory>(),
            sp.GetRequiredService<ILogger<GoalSubGoalExecutor>>(),
            sp.GetRequiredService<IPromptManager>(),
            sp.GetRequiredService<GoalContextState>(),
            sp.GetService<IVerificationProvider>(),
            sp.GetService<Services.Lsp.LspDiagnosticRegistry>()));
        services.AddSingleton<IGoalStepExecutionService>(sp => sp.GetRequiredService<GoalSubGoalExecutor>());
        services.AddSingleton<IGoalCompletionService>(sp => new GoalCompletionService(
            sp.GetRequiredService<IGoalRunStore>(),
            sp.GetRequiredService<IGoalWorkspaceService>(),
            sp.GetRequiredService<IGoalStepExecutionService>(),
            sp.GetService<IVerificationProvider>(),
            sp.GetService<Services.Lsp.LspDiagnosticRegistry>()));
        services.AddSingleton<IGoalWorkflowRuntimeFactory, GoalWorkflowRuntimeFactory>();

        return services;
    }
}
