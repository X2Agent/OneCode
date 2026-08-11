using Microsoft.Agents.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using OneCode.App.Services;
using OneCode.App.Services.Agent;
using OneCode.App.Services.BuildMode;
using OneCode.App.Services.Context;
using OneCode.App.Services.GoalMode;
using OneCode.App.Services.Lsp;
using OneCode.App.Services.Memory;
using OneCode.App.Services.PlanMode;
using OneCode.App.Services.Skills;
using OneCode.App.Session;
using OneCode.App.Tools;
using OneCode.Core.Memory;
using OneCode.Core.Models;
using OneCode.Core.Permissions;
using OneCode.Core.Prompt;
using OneCode.Core.Tasks;
using OneCode.Infrastructure.Agent;

namespace OneCode.Tests.TestSupport;

/// <summary>
/// Test helpers that construct real context-provider builders with substituted deps.
/// </summary>
public static class TestAgentContextProviderAssembly
{
    public static (SharedContextProviderBuilder Shared, MainModeContextProviderBuilder Main) Create(
        ISessionManager? sessionManager = null,
        IModelManager? modelManager = null,
        IPermissionModeProvider? modeProvider = null,
        IPromptManager? promptManager = null,
        IPlanModeService? planModeService = null,
        IPlanWorkflowApplicationService? planWorkflowService = null)
    {
        sessionManager ??= Substitute.For<ISessionManager>();
        modelManager ??= new ModelManager(TestConfigManager.Create(), new ModelCatalogStore());
        modeProvider ??= new PermissionModeProvider(TestConfigManager.Create());
        promptManager ??= new PromptManager();
        planModeService ??= Substitute.For<IPlanModeService>();
        planWorkflowService ??= Substitute.For<IPlanWorkflowApplicationService>();

        var memory = new AgentMemoryDependencies(
            Substitute.For<IMemoryService>(),
            new SessionMemoryService(NullLogger<SessionMemoryService>.Instance),
            sessionManager);
        var runtime = new AgentRuntimeContextDependencies(
            new ConversationShellExecutorManager(NullLogger<ConversationShellExecutorManager>.Instance),
            new HyperlightCodeActService(NullLogger<HyperlightCodeActService>.Instance),
            new LspDiagnosticRegistry(),
            new TaskContextProvider(Substitute.For<ITaskService>()));

        var shared = new SharedContextProviderBuilder(
            NullLoggerFactory.Instance,
            new SkillProviderHolder(new AgentSkillsProviderBuilder().Build()),
            memory,
            runtime,
            modelManager);

        var main = new MainModeContextProviderBuilder(
            shared,
            planModeService,
            modeProvider,
            promptManager,
            new BuildModeAttachmentProvider(modeProvider, promptManager),
            new PlanExecutionContextProvider(planWorkflowService, modeProvider),
            new GoalContextState());

        return (shared, main);
    }
}

public static class TestTokenEstimators
{
    public static OneCode.Infrastructure.TokenEstimator Default { get; } = new();
}
