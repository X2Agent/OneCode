using Microsoft.Extensions.DependencyInjection;
using OneCode.Infrastructure;

namespace OneCode.App.Services.Agent;

/// <summary>
/// Agent 运行时领域 DI 注册——与 Agent 管线实现（<see cref="MainAgentRunner"/> /
/// <see cref="AgentPipelineAssembly"/>）同目录维护。由组合根 <see cref="OneCode.App.OneCodeApp"/> 显式调用。
/// </summary>
public static class AgentServiceCollectionExtensions
{
    public static IServiceCollection AddAgentRuntimeServices(this IServiceCollection services)
    {
        services.AddHyperlightCodeAct();
        services.AddSingleton<AgentMemoryDependencies>();
        services.AddSingleton<AgentRuntimeContextDependencies>();
        services.AddSingleton<SharedContextProviderBuilder>();
        services.AddSingleton<MainModeContextProviderBuilder>();
        services.AddSingleton<AgentContextPipeline>();
        services.AddSingleton<AgentPipelineAssembly>();
        services.AddSingleton<SubAgentPipelineFactory>();
        services.AddSingleton<CompactionStrategyFactory>();
        services.AddSingleton<AgentSessionPersistence>();

        // 待办清单投影（Harness TodoProvider 会话状态 → 统一事件总线 → TUI 横条）。
        // 与 PlanCardPublisher 同构：App 层发射器 + TUI 宿主订阅。
        services.AddSingleton<TodoProjectionService>();

        services.AddSingleton<MainAgentRunner>();
        services.AddSingleton<IMainAgentRunner>(sp => sp.GetRequiredService<MainAgentRunner>());

        // MAF 工作流运行时基础设施（物理上位于本目录：DurableWorkflowHost 等）。
        // IWorkflowRunRegistry / IOperationLedger 由 AddPlatformServices 统一注册。
        services.AddSingleton<IWorkflowCheckpointStoreFactory, WorkflowCheckpointStoreFactory>();
        services.AddSingleton<IWorkflowEventAdapter, WorkflowEventAdapter>();
        services.AddSingleton<IWorkflowRequestAdapter, WorkflowRequestAdapter>();
        services.AddSingleton<IDurableWorkflowHost, DurableWorkflowHost>();

        // ParallelAgentsTool compiles a fresh MAF workflow and executor set for every invocation.
        services.AddSingleton<AgentTaskWorkflowCompiler>();
        services.AddSingleton<AgentTaskWorkflowHost>();

        return services;
    }
}
