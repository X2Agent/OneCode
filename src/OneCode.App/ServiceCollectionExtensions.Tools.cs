using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using OneCode.App.Query;
using OneCode.App.Services;
using OneCode.App.Services.Cache;
using OneCode.App.Services.Context;
using OneCode.App.Services.Lsp;
using OneCode.App.Services.Mcp;
using OneCode.App.Services.PlanMode;
using OneCode.App.Session;
using OneCode.App.Tools;
using OneCode.Automation;
using OneCode.Infrastructure.Mcp;

namespace OneCode.App;

public static partial class ServiceCollectionExtensions
{
    public static IServiceCollection RegisterToolServices(this IServiceCollection services)
    {
        services.AddSingleton<WebFetchCache>();
        services.AddSingleton<LspDiagnosticRegistry>();
        services.AddSingleton<EnhancedLspService>();
        services.AddSingleton<ILspNotifier, LspNotifier>();
        services.AddSingleton<McpConnectionManager>();
        services.AddSingleton<IMcpConnectionManager>(sp => sp.GetRequiredService<McpConnectionManager>());

        services.AddSingleton<TaskContextProvider>();
        services.AddSingleton<FileContentCache>();
        services.AddSingleton<IFileContentCache>(sp => sp.GetRequiredService<FileContentCache>());

        services.AddSingleton<ToolMetadataRegistry>();
        // Composition root owns IServiceProvider capture in Lazy — ToolCatalog itself does not.
        services.AddSingleton<ToolCatalog>(sp =>
        {
            var registrations = sp.GetServices<ToolRegistration>().ToList();
            var metadata = sp.GetRequiredService<ToolMetadataRegistry>();
            var mcp = sp.GetService<IMcpConnectionManager>();
            var staticTools = new Lazy<List<AIFunction>>(
                () => ToolCatalog.BuildStaticTools(sp, registrations, metadata),
                LazyThreadSafetyMode.ExecutionAndPublication);
            return new ToolCatalog(staticTools, metadata, mcp);
        });
        services.AddSingleton<IToolCatalog>(sp => sp.GetRequiredService<ToolCatalog>());
        services.AddSingleton<IToolCapabilityResolver, ToolCapabilityResolver>();
        services.AddSingleton<SessionToolSetManager>();
        services.AddSingleton<ISessionToolSetManager>(sp => sp.GetRequiredService<SessionToolSetManager>());

        // After tools: SessionManager requires ISessionToolSetManager.
        services.AddSingleton<SessionManager>(sp =>
        {
            var store = sp.GetRequiredService<ISessionStore>();
            var logger = sp.GetRequiredService<ILogger<SessionManager>>();
            var sessionOptions = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<SessionOptions>>().Value;
            return new SessionManager(store, logger, sessionOptions.InitialWorkingDirectory,
                hookExecutionService: sp.GetRequiredService<IHookExecutionService>(),
                shellExecutorCleanup: sp.GetRequiredService<IShellExecutorCleanup>(),
                tokenUsageTracker: sp.GetRequiredService<Services.Observability.ITokenUsageTracker>(),
                sessionIdHolder: sp.GetRequiredService<SessionIdHolder>(),
                sessionToolSetManager: sp.GetRequiredService<ISessionToolSetManager>());
        });
        services.AddSingleton<ISessionManager>(sp => sp.GetRequiredService<SessionManager>());
        services.AddSingleton<ISessionConversationAccess>(sp => sp.GetRequiredService<SessionManager>());
        services.AddSingleton<ISessionWorkingDirectory>(sp => sp.GetRequiredService<SessionManager>());

        services.AddTool<BashTool>("Bash", nameof(BashTool.ExecuteAsync), ToolRisk.Dynamic,
            aliases: ["shell", "sh"], concurrency: false, searchHint: "execute a Unix/Linux shell command");
        services.AddSingleton<ConversationShellExecutorManager>();
        services.AddSingleton<IShellExecutorCleanup>(sp => sp.GetRequiredService<ConversationShellExecutorManager>());
        services.AddTool<PowerShellTool>("PowerShell", nameof(PowerShellTool.ExecuteAsync), ToolRisk.Dynamic,
            aliases: ["ps"], concurrency: false, searchHint: "execute a PowerShell command");

        services.AddTool<ReadTool>("Read", nameof(ReadTool.ReadAsync), ToolRisk.ReadOnly, searchHint: "read file contents with offset/limit");
        services.AddTool<WriteTool>("Write", nameof(WriteTool.WriteAsync), ToolRisk.Destructive, concurrency: false, searchHint: "create or overwrite a file",
            category: ToolCategory.FileEdit | ToolCategory.FileWrite);
        services.AddTool<EditTool>("Edit", nameof(EditTool.EditAsync), ToolRisk.Destructive, concurrency: false, searchHint: "search-replace edit a file",
            category: ToolCategory.FileEdit | ToolCategory.FileWrite);
        services.AddTool<LSTool>("LS", nameof(LSTool.ListAsync), ToolRisk.ReadOnly, searchHint: "list directory contents");
        services.AddTool<GlobTool>("Glob", nameof(GlobTool.GlobAsync), ToolRisk.ReadOnly, searchHint: "glob pattern file search");
        services.AddTool<GrepTool>("Grep", nameof(GrepTool.SearchAsync), ToolRisk.ReadOnly, searchHint: "regex content search (ripgrep)");
        services.AddTool<FindReferencesTool>("FindReferences", nameof(FindReferencesTool.FindAsync), ToolRisk.ReadOnly, searchHint: "find code references",
            loadPolicy: ToolLoadPolicy.Contextual, keywords: ["reference"]);
        services.AddTool<ApplyWorkspaceEditTool>("ApplyWorkspaceEdit", nameof(ApplyWorkspaceEditTool.ApplyAsync), ToolRisk.Destructive, concurrency: false, searchHint: "apply an LSP workspace edit to files",
            loadPolicy: ToolLoadPolicy.Deferred, keywords: ["workspace edit"], category: ToolCategory.FileWrite);

        services.AddSingleton<BrowserLauncher>();
        services.AddSingleton<PlaywrightRenderer>();
        services.AddTool<WebFetchTool>("WebFetch", nameof(WebFetchTool.FetchAsync), ToolRisk.ReadOnly, searchHint: "fetch web page content as markdown",
            loadPolicy: ToolLoadPolicy.Contextual, keywords: ["web fetch", "fetch web", "url"]);
        services.AddTool<WebSearchTool>("WebSearch", nameof(WebSearchTool.SearchAsync), ToolRisk.ReadOnly, searchHint: "search the web",
            loadPolicy: ToolLoadPolicy.Contextual, keywords: ["web search", "search web"]);

        services.AddTool<TaskTool>("Task", nameof(TaskTool.ExecuteAsync), ToolRisk.Safe, searchHint: "manage background tasks (create/update/get/list/stop/output)",
            loadPolicy: ToolLoadPolicy.Contextual, keywords: ["task", "background task"], category: ToolCategory.PlanAllowed);
        services.AddTool<BackgroundRunTool>("BackgroundRun", nameof(BackgroundRunTool.RunAsync), ToolRisk.Destructive, concurrency: false, searchHint: "run command in background",
            loadPolicy: ToolLoadPolicy.Contextual, keywords: ["background"]);
        services.AddTool<BackgroundWaitTool>("BackgroundWait", nameof(BackgroundWaitTool.WaitAsync), ToolRisk.ReadOnly, searchHint: "wait for background task",
            loadPolicy: ToolLoadPolicy.Contextual, keywords: ["background"]);

        // AgentTool resolves via normal DI (ICacheSafeParamsProvider) — no ChatService locator.
        services.AddTool<AgentTool>("Agent", nameof(AgentTool.RunAgentAsync), ToolRisk.Safe, searchHint: "run a sub-agent on a delegated task",
            loadPolicy: ToolLoadPolicy.Contextual, keywords: ["agent", "sub-agent", "delegate"], category: ToolCategory.PlanAllowed);
        services.AddTool<ParallelAgentsTool>("ParallelAgents", nameof(ParallelAgentsTool.RunParallelAsync), ToolRisk.Safe, searchHint: "run sub-agents with DAG dependencies",
            loadPolicy: ToolLoadPolicy.Contextual, keywords: ["agent", "parallel"], category: ToolCategory.PlanAllowed);

        services.AddSingleton<TuiInteractionBridge>();
        services.AddSingleton<IUserQuestionService, UserQuestionService>();
        services.AddSingleton<IClarificationInteractionService, ClarificationInteractionService>();

        services.AddSingleton<AskUserQuestionTool>();
        services.AddTool<AskUserQuestionTool>("AskUserQuestion", nameof(AskUserQuestionTool.AskAsync), ToolRisk.ReadOnly, searchHint: "ask the user one blocking question",
            category: ToolCategory.PlanAllowed);
        services.AddTool<AskUserQuestionTool>("AskUserQuestions", nameof(AskUserQuestionTool.AskMultipleAsync), ToolRisk.ReadOnly, searchHint: "ask the user multiple related questions in one wizard",
            category: ToolCategory.PlanAllowed);
        services.AddTool<SymbolSearchTool>("SymbolSearch", nameof(SymbolSearchTool.SymbolSearchAsync), ToolRisk.ReadOnly, searchHint: "search code symbols",
            loadPolicy: ToolLoadPolicy.Contextual, keywords: ["symbol"]);
        services.AddTool<LspTool>("Lsp", nameof(LspTool.ExecuteLspAsync), ToolRisk.ReadOnly, searchHint: "perform language-server operations",
            loadPolicy: ToolLoadPolicy.Contextual, keywords: ["lsp", "language server"]);
        services.AddTool<ChromeDevToolsTool>("ChromeDevTools", nameof(ChromeDevToolsTool.ExecuteDevToolsAsync), ToolRisk.Dynamic, searchHint: "inspect browser via DevTools protocol",
            loadPolicy: ToolLoadPolicy.Deferred, keywords: ["browser", "chrome", "devtools"]);
        services.AddTool<EnterWorktreeTool>("EnterWorktree", nameof(EnterWorktreeTool.EnterAsync), ToolRisk.Destructive, concurrency: false, searchHint: "enter or create a git worktree",
            loadPolicy: ToolLoadPolicy.Deferred, keywords: ["worktree"]);
        services.AddTool<ExitWorktreeTool>("ExitWorktree", nameof(ExitWorktreeTool.ExitAsync), ToolRisk.Destructive, concurrency: false, searchHint: "exit a git worktree",
            loadPolicy: ToolLoadPolicy.Deferred, keywords: ["worktree"]);

        // Plan authoring tools are available only inside the Plan capability boundary.
        // SubmitPlan closes immediately in FinalizingPlanRun; approval is a persisted command.
        services.AddSingleton<PlanCardPublisher>();
        services.AddTool<CreatePlanTool>("SavePlan", nameof(CreatePlanTool.SavePlanAsync), ToolRisk.Safe, searchHint: "save plan draft without exiting plan mode",
            loadPolicy: ToolLoadPolicy.Contextual, keywords: ["plan"], category: ToolCategory.PlanAllowed);
        services.AddTool<CreatePlanTool>("SubmitPlan", nameof(CreatePlanTool.SubmitPlanAsync), ToolRisk.Safe, searchHint: "submit finalized plan for persisted user approval",
            loadPolicy: ToolLoadPolicy.Contextual, keywords: ["plan", "submit", "approve"], category: ToolCategory.PlanAllowed);

        // Approved Build runs must persist structured progress and verification evidence.
        services.AddTool<PlanExecutionTool>("UpdatePlanStep", nameof(PlanExecutionTool.UpdatePlanStepAsync), ToolRisk.Safe,
            searchHint: "update approved plan step execution status", loadPolicy: ToolLoadPolicy.Always,
            keywords: ["plan", "step", "progress"]);
        services.AddTool<PlanExecutionTool>("CompletePlanExecution", nameof(PlanExecutionTool.CompletePlanExecutionAsync), ToolRisk.Safe,
            searchHint: "finish approved plan execution and start verification", loadPolicy: ToolLoadPolicy.Always,
            keywords: ["plan", "complete", "verify"]);
        services.AddTool<PlanExecutionTool>("CompletePlanVerification", nameof(PlanExecutionTool.CompletePlanVerificationAsync), ToolRisk.Safe,
            searchHint: "persist approved plan verification evidence", loadPolicy: ToolLoadPolicy.Always,
            keywords: ["plan", "verification", "evidence"]);

        // Cron tools (registered via AddCronTools in OneCode.Automation)
        services.AddCronTools();

        services.AddTool<ListMcpResourcesTool>("ListMcpResources", nameof(ListMcpResourcesTool.ListResourcesAsync), ToolRisk.ReadOnly, searchHint: "list MCP resources",
            loadPolicy: ToolLoadPolicy.Contextual, keywords: ["mcp", "resource"]);
        services.AddTool<ReadMcpResourceTool>("ReadMcpResource", nameof(ReadMcpResourceTool.ReadResourceAsync), ToolRisk.ReadOnly, searchHint: "read an MCP resource",
            loadPolicy: ToolLoadPolicy.Contextual, keywords: ["mcp", "resource"]);

        // ToolSearch (needs runtime metadata access)
        services.AddToolInstance("ToolSearch",
            sp => AIFunctionFactory.Create(
                new ToolSearchTool(
                    sp.GetRequiredService<ToolMetadataRegistry>(),
                    sp.GetRequiredService<ISessionToolSetManager>()).Search,
                name: "ToolSearch"),
            ToolRisk.ReadOnly, visible: true, searchHint: "search available tools by keyword to discover non-core tools");

        return services;
    }
}
