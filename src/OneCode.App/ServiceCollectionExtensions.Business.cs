using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using OneCode.App.Commands;
using OneCode.App.Query;
using OneCode.App.Services;
using OneCode.App.Services.Agent;
using OneCode.App.Services.BuildMode;
using OneCode.App.Services.Compact;
using OneCode.App.Services.Context;
using OneCode.App.Services.Cron;
using OneCode.App.Services.GoalMode;
using OneCode.App.Services.Hooks;
using OneCode.App.Services.Hooks.Notifications;
using OneCode.App.Services.Observability;
using OneCode.App.Services.PlanMode;
using OneCode.App.Services.Setup;
using OneCode.App.Tui;
using OneCode.Automation;
using OneCode.Automation.Cron;
using OneCode.Core.Build;
using OneCode.Core.Cost;
using OneCode.Core.Cron;
using OneCode.Core.Goals;
using OneCode.Core.Hooks.Notifications;
using OneCode.Infrastructure.Build;
using OneCode.Infrastructure.Goals;
using OneCode.Infrastructure.Keybindings;
using OneCode.Core.Models;
using OneCode.Core.Prompt;
using OneCode.Core.Permissions.Yolo;
using OneCode.Infrastructure;
using OneCode.Infrastructure.Api;
using OneCode.Infrastructure.Config;
using OneCode.Infrastructure.Model;

namespace OneCode.App;

public static partial class ServiceCollectionExtensions
{
    public static IServiceCollection RegisterBusinessServices(this IServiceCollection services)
    {
        services.AddSingleton<ModelCatalogStore>();
        services.AddSingleton<IModelCatalog>(sp => sp.GetRequiredService<ModelCatalogStore>());

        services.AddSingleton<ModelManager>();
        services.AddSingleton<IModelManager>(sp => sp.GetRequiredService<ModelManager>());

        services.AddSingleton<CostTracker>(sp =>
        {
            var logger = sp.GetRequiredService<ILogger<CostTracker>>();
            var catalog = sp.GetRequiredService<IModelCatalog>();
            var tracker = new CostTracker(logger, modelCatalog: catalog);
            tracker.SyncPricingFromCatalog();
            return tracker;
        });
        services.AddSingleton<ICostTracker>(sp => sp.GetRequiredService<CostTracker>());

        RegisterHookSubsystem(services);
        RegisterPermissionSubsystem(services);

        services.AddSingleton<KeybindingLoader>();
        services.AddSingleton<IDynamicCommandSource, SkillCommandSource>();
        services.AddSingleton<IDynamicCommandSource, McpCommandSource>();
        services.AddSingleton<ICronParser, OneCode.Automation.Cron.CronosCronParser>();
        services.AddSingleton<CronJobExecutor>();
        services.AddSingleton<ICronJobExecutor>(sp => sp.GetRequiredService<CronJobExecutor>());
        services.AddCronScheduler();

        services.AddSingleton<ModelsDevClient>();
        services.AddSingleton<IModelCatalogCache>(sp =>
        {
            var client = sp.GetRequiredService<ModelsDevClient>();
            var catalogStore = sp.GetRequiredService<ModelCatalogStore>();
            var logger = sp.GetRequiredService<ILogger<ModelCatalogCacheService>>();
            var cache = new ModelCatalogCacheService(client, catalogStore, logger);
            cache.TryLoadFromCache();
            return cache;
        });
        services.AddModelCatalogRefresh();

        services.AddSingleton<GitInfo>();
        services.AddSingleton<ContextBuilder>();
        services.AddSingleton<ConfigManager>(_ =>
        {
            var userConfigDir = PathsHelper.GetUserConfigDir();
            var projectConfigDir = Path.Combine(Environment.CurrentDirectory, Constants.App.ConfigDirName);
            return new ConfigManager(userConfigDir, projectConfigDir);
        });
        services.AddSingleton<IConfigManager>(sp => sp.GetRequiredService<ConfigManager>());
        services.AddSingleton<TrustService>();
        services.AddSingleton<StartupFlowCoordinator>();
        services.AddSingleton<ReleaseNotesService>();
        services.AddSingleton<UpgradeService>();

        services.AddSingleton<TokenUsageTracker>();
        services.AddSingleton<ITokenUsageTracker>(sp => sp.GetRequiredService<TokenUsageTracker>());
        services.AddSingleton<TokenBreakdownEstimator>();
        services.AddSingleton<ITokenBreakdownEstimator>(sp => sp.GetRequiredService<TokenBreakdownEstimator>());

        services.AddSingleton<IPlanAggregateStore, PlanAggregateStore>();
        services.AddSingleton<IPlanWorkflowApplicationService, PlanWorkflowApplicationService>();
        services.AddSingleton<IPlanAgentRunDispatcher, PlanAgentRunDispatcher>();
        services.AddSingleton<PlanExecutionRecoveryService>();
        services.AddHostedService(sp => sp.GetRequiredService<PlanExecutionRecoveryService>());

        services.AddSingleton<JsonBuildRunStore>();
        services.AddSingleton<IBuildRunStore>(sp => sp.GetRequiredService<JsonBuildRunStore>());
        services.AddSingleton<IBuildRunEventStore>(sp => sp.GetRequiredService<JsonBuildRunStore>());
        services.AddSingleton<IWorkspaceFingerprintProvider, WorkspaceFingerprintProvider>();
        services.AddSingleton<RequirementAssessmentService>();
        services.AddSingleton<BuildStateTransitionService>();
        services.AddSingleton<IBuildRunCoordinator, BuildRunCoordinator>();

        services.AddSingleton<ChatSessionDependencies>();
        services.AddSingleton<ChatObservabilityDependencies>();
        services.AddSingleton<IToolProtocolValidator, ToolProtocolValidator>();
        services.AddSingleton<ChatService>();
        services.AddSingleton<IConversationRunner>(sp => sp.GetRequiredService<ChatService>());
        services.AddSingleton<OneCode.Core.Tools.ICacheSafeParamsProvider>(
            sp => sp.GetRequiredService<ChatService>());

        services.AddSingleton<InputQueue>();

        services.AddSingleton<PermissionModeProvider>();
        services.AddSingleton<IPermissionModeProvider>(sp => sp.GetRequiredService<PermissionModeProvider>());

        services.AddSingleton<OneCode.App.Services.BuildMode.BuildModeAttachmentProvider>(sp =>
            new OneCode.App.Services.BuildMode.BuildModeAttachmentProvider(
                sp.GetRequiredService<IPermissionModeProvider>(),
                sp.GetRequiredService<IPromptManager>()));

        services.AddSingleton<OneCode.App.Services.PlanMode.PlanExecutionContextProvider>(sp =>
            new OneCode.App.Services.PlanMode.PlanExecutionContextProvider(
                sp.GetRequiredService<IPlanWorkflowApplicationService>(),
                sp.GetRequiredService<IPermissionModeProvider>()));

        services.AddHyperlightCodeAct();
        services.AddSingleton<AgentMemoryDependencies>();
        services.AddSingleton<AgentRuntimeContextDependencies>();
        services.AddSingleton<SharedContextProviderBuilder>();
        services.AddSingleton<MainModeContextProviderBuilder>();
        services.AddSingleton<AgentPipelineAssembly>();
        services.AddSingleton<SubAgentPipelineFactory>();
        services.AddSingleton<CompactionProviderBuilder>();
        services.AddSingleton<AgentSessionStore>();

        services.AddSingleton<MainAgentRunner>();
        services.AddSingleton<IMainAgentRunner>(sp => sp.GetRequiredService<MainAgentRunner>());

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

    private static void RegisterHookSubsystem(IServiceCollection services)
    {
        services.AddSingleton<GlobHookMatcher>();
        services.AddSingleton<HookSettingsLoader>();
        services.AddSingleton<HookRegistry>();
        services.AddSingleton<HookPolicyService>();

        services.AddKeyedSingleton<IHookExecutor, CommandHookExecutor>(HookType.Command);
        services.AddKeyedSingleton<IHookExecutor, NotificationHookExecutor>(HookType.Notification);
        services.AddKeyedSingleton<IHookExecutor, HttpHookExecutor>(HookType.Http);

        services.AddSingleton<INotificationProvider, FeishuNotificationProvider>();
        services.AddSingleton<INotificationProvider, WeChatWorkNotificationProvider>();

        services.AddHttpClient<FeishuNotificationProvider>();
        services.AddHttpClient<WeChatWorkNotificationProvider>();

        services.AddSingleton<HookExecutionService>();
        services.AddSingleton<IHookExecutionService>(sp => sp.GetRequiredService<HookExecutionService>());
        services.AddSingleton<HookConfigBootstrapper>();
    }

    private static void RegisterPermissionSubsystem(IServiceCollection services)
    {
        services.AddSingleton<YoloRuleStore>();
        services.AddSingleton<OneCode.Infrastructure.Permissions.Yolo.YoloRuleFileStore>();
        services.AddSingleton<IYoloRuleFileStore>(sp => sp.GetRequiredService<OneCode.Infrastructure.Permissions.Yolo.YoloRuleFileStore>());
        services.AddSingleton<YoloClassifier>();
        services.AddSingleton<IYoloClassifier>(sp => sp.GetRequiredService<YoloClassifier>());
        services.AddSingleton<IPermissionChecker, PermissionChecker>();
        services.AddYoloRuleStoreLoader();
    }
}
