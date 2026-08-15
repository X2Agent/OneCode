using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OneCode.App.Services;
using OneCode.App.Services.Agent;
using OneCode.App.Services.AutoDream;
using OneCode.App.Services.Coordinator;
using OneCode.App.Services.Notifier;
using OneCode.Infrastructure;
using OneCode.Infrastructure.Abstractions;
using OneCode.Infrastructure.Media;
using OneCode.Infrastructure.Remote;
using OneCode.Infrastructure.Teams;
using OneCode.Infrastructure.Workflows;

namespace OneCode.App;

public static partial class ServiceCollectionExtensions
{
    public static IServiceCollection RegisterAdvancedServices(
        this IServiceCollection services,
        IConfiguration configuration)
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
        services.AddSingleton<ITeamQualityGateValidator, TeamChangeScopeQualityGateValidator>();
        services.AddSingleton<ITeamQualityGateValidator, TeamWorkspaceCleanlinessQualityGateValidator>();
        services.AddSingleton<ITeamQualityGateValidator, TeamSecurityQualityGateValidator>();
        services.AddSingleton<ITeamQualityGateValidator, TeamBuildQualityGateValidator>();
        services.AddSingleton<ITeamQualityGateValidator, TeamUnitTestQualityGateValidator>();
        services.AddSingleton<ITeamQualityGateValidator, TeamIntegrationTestQualityGateValidator>();
        services.AddSingleton<ITeamQualityGateValidator, TeamLspDiagnosticsQualityGateValidator>();
        services.AddSingleton<ITeamQualityGateValidator, TeamAcceptanceCriteriaQualityGateValidator>();
        services.AddSingleton<TeamQualityGateRunner>();
        services.AddSingleton<DeliveryReportBuilder>();
        services.AddSingleton<TeamRunApplicationService>();
        // Team M5：将批准 TaskGraph 通过共享 MAF Durable Workflow Host 编排（Fan-out/Fan-in Barrier）。
        services.AddSingleton<TeamTaskWorkflowCompiler>();
        services.AddSingleton<TeamApprovalWorkflowCompiler>();
        services.AddSingleton<TeamClarificationWorkflowCompiler>();
        services.AddSingleton<TeamTaskWorkflowHost>();
        services.AddSingleton<TeamApprovalWorkflowHost>();
        services.AddSingleton<TeamClarificationWorkflowHost>();
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
            sp.GetRequiredService<TeamApprovalWorkflowHost>(),
            sp.GetRequiredService<TeamClarificationWorkflowHost>(),
            sp.GetRequiredService<Core.Coordinator.ITeamRunStore>(),
            sp.GetService<Core.Workflows.IOperationLedger>()));
        services.AddSingleton<Core.Coordinator.ITeamOrchestrationService>(sp =>
            sp.GetRequiredService<TeamOrchestrationService>());
        services.AddSingleton<WorkerAgentService>();
        services.AddSingleton<Core.Tools.IAgentRunner>(sp => sp.GetRequiredService<WorkerAgentService>());
        // ParallelAgentsTool compiles a fresh MAF workflow and executor set for every invocation.
        services.AddSingleton<AgentTaskWorkflowCompiler>();
        services.AddSingleton<AgentTaskWorkflowHost>();
        services.AddSingleton<Core.Workflows.IWorkflowRunRegistry, JsonWorkflowRunRegistry>();
        services.AddSingleton<Core.Workflows.IOperationLedger>(
            new OneCode.Infrastructure.Workflows.FileOperationLedger());
        services.AddSingleton<IWorkflowCheckpointStoreFactory, WorkflowCheckpointStoreFactory>();
        services.AddSingleton<IWorkflowEventAdapter, WorkflowEventAdapter>();
        services.AddSingleton<IWorkflowRequestAdapter, WorkflowRequestAdapter>();
        services.AddSingleton<IDurableWorkflowHost, DurableWorkflowHost>();
        services.AddSingleton<Services.BuildMode.ControlledBuildAttemptWorkflowCompiler>();
        services.AddSingleton<Services.BuildMode.ControlledBuildAttemptHost>();

        // AutoDream: 后台记忆整合服务。注册为 Singleton + HostedService：
        // - Singleton：供 /memory autodream trigger 命令通过 DI 获取并调用 Trigger()
        // - HostedService：让 BackgroundService.ExecuteAsync 随宿主生命周期自动启停（1h 轮询是唯一自动触发路径）
        services.AddSingleton<AutoDreamAgentDependencies>();
        services.AddSingleton<AutoDreamStorageDependencies>();
        services.AddSingleton<AutoDreamService>();
        services.AddHostedService(sp => sp.GetRequiredService<AutoDreamService>());

        services.AddSingleton<SshRemoteService>();

        services.AddSingleton<ICodeIndexService, CodeIndexService>();
        services.AddSingleton<CodeIndexHotReloader>();

        // VCR (录像/回放) — Infrastructure 层基础设施，注册下沉到 AddVcrServices()。
        // 未设 ONECODE_VCR 环境变量时零开销透传，不影响生产路径。
        services.AddVcrServices();

        services.AddSingleton<NotifierService>();
        services.AddSingleton<INotifierService>(sp => sp.GetRequiredService<NotifierService>());

        services.AddSingleton<ImagePipeline>();

        return services;
    }
}
