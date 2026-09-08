using Microsoft.Extensions.DependencyInjection;
using OneCode.App.Services.Agent;
using OneCode.App.Services.Runtime;
using OneCode.Infrastructure;
using OneCode.Infrastructure.Teams;

namespace OneCode.App.Services.Coordinator;

/// <summary>
/// Team 编排领域 DI 注册——与编排实现（<see cref="TeamOrchestrationService"/> /
/// <see cref="TeamRunApplicationService"/>）同目录维护；质量门校验器（Runtime）与
/// Forked Agent 管线（Agent）在此一并组装。由组合根 <see cref="OneCode.App.OneCodeApp"/> 显式调用。
/// </summary>
public static class TeamServiceCollectionExtensions
{
    public static IServiceCollection AddTeamServices(this IServiceCollection services)
    {
        services.AddSingleton<Core.Tools.IVerificationProvider, Infrastructure.Tools.GenericVerificationProvider>();
        services.AddSingleton<ForkedAgentRuntimeDependencies>();
        services.AddSingleton<ForkedAgentRunner>();
        services.AddSingleton<TeamAgentToolSources>();
        services.AddSingleton<TeamAgentPipelineDependencies>();
        services.AddSingleton<TeamAgentFactory>();

        services.AddSingleton<TeamWorkflowRunner>();
        services.AddSingleton<Core.Coordinator.ITeamRunStore>(_ =>
            new JsonTeamRunStore(Path.Combine(PathsHelper.GetUserConfigDir(), "team-runs")));
        services.AddSingleton<TeamRunStateMachine>();
        services.AddSingleton<IClarificationQuestionGenerator, ClarificationQuestionGenerator>();
        services.AddSingleton<TeamRequirementService>();
        services.AddSingleton<IWorkflowQualityGateValidator, WorkflowChangeScopeQualityGateValidator>();
        services.AddSingleton<IWorkflowQualityGateValidator, WorkflowWorkspaceCleanlinessQualityGateValidator>();
        services.AddSingleton<IWorkflowQualityGateValidator, WorkflowSecurityQualityGateValidator>();
        services.AddSingleton<IWorkflowQualityGateValidator, WorkflowBuildQualityGateValidator>();
        services.AddSingleton<IWorkflowQualityGateValidator, WorkflowUnitTestQualityGateValidator>();
        services.AddSingleton<IWorkflowQualityGateValidator, WorkflowIntegrationTestQualityGateValidator>();
        services.AddSingleton<IWorkflowQualityGateValidator, WorkflowLspDiagnosticsQualityGateValidator>();
        services.AddSingleton<IWorkflowQualityGateValidator, WorkflowAcceptanceCriteriaQualityGateValidator>();
        services.AddSingleton<WorkflowQualityGateRunner>();
        services.AddSingleton<DeliveryReportBuilder>();
        // C2: 注入工作区指纹 provider（可选），供 Succeeded 任务落库记录指纹与恢复世代对账。
        services.AddSingleton(sp => new TeamRunApplicationService(
            sp.GetRequiredService<Core.Coordinator.ITeamRunStore>(),
            sp.GetRequiredService<TeamRunStateMachine>(),
            sp.GetRequiredService<WorkflowQualityGateRunner>(),
            sp.GetRequiredService<DeliveryReportBuilder>(),
            sp.GetService<Core.Build.IWorkspaceFingerprintProvider>()));
        // Team M5：将批准 TaskGraph 通过共享 MAF Durable Workflow Host 编排（Fan-out/Fan-in Barrier）。
        services.AddSingleton<TeamTaskWorkflowCompiler>();
        services.AddSingleton<TeamApprovalWorkflowCompiler>();
        services.AddSingleton<TeamClarificationWorkflowCompiler>();
        services.AddSingleton<TeamTaskWorkflowHost>();
        services.AddSingleton<TeamApprovalWorkflowHost>();
        services.AddSingleton<TeamClarificationWorkflowHost>();
        services.AddSingleton<RequestPortGate>();
        // Factory: ctor is internal (takes internal TeamWorkflowRunner); DI cannot auto-bind it.
        services.AddSingleton(sp => new TeamOrchestrationService(
            sp.GetRequiredService<TeamWorkflowRunner>(),
            sp.GetRequiredService<ILoggerFactory>(),
            sp.GetRequiredService<ILogger<TeamOrchestrationService>>(),
            sp.GetRequiredService<TeamRunApplicationService>(),
            sp.GetRequiredService<TeamRequirementService>(),
            sp.GetRequiredService<IClarificationInteractionService>(),
            sp.GetRequiredService<Core.Tools.IWorkingDirectoryAccessor>(),
            sp.GetRequiredService<TeamTaskWorkflowHost>(),
            sp.GetRequiredService<TeamClarificationWorkflowHost>(),
            sp.GetRequiredService<RequestPortGate>(),
            sp.GetRequiredService<Core.Coordinator.ITeamRunStore>(),
            sp.GetService<Core.Workflows.IOperationLedger>()));
        services.AddSingleton<Core.Coordinator.ITeamOrchestrationService>(sp =>
            sp.GetRequiredService<TeamOrchestrationService>());
        services.AddSingleton<WorkerAgentService>();
        services.AddSingleton<Core.Tools.IAgentRunner>(sp => sp.GetRequiredService<WorkerAgentService>());

        return services;
    }
}
