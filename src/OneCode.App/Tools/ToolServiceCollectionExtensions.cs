using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using OneCode.App.Query;
using OneCode.App.Services;
using OneCode.App.Services.Streaming;
using OneCode.Automation;
using OneCode.Core.Mcp;

namespace OneCode.App.Tools;

/// <summary>
/// 工具目录与工具注册流 DI 注册——与工具实现（ToolCatalog / 各 Tool）同目录维护。
/// <c>AddTool</c> 流的调用顺序即 <see cref="ToolRegistration"/> 的 IEnumerable 注入顺序，
/// 重排会改变工具目录装配结果——除非有意变更，否则保持现有顺序。
/// 由组合根 <see cref="OneCode.App.OneCodeApp"/> 显式调用。
/// </summary>
public static class ToolServiceCollectionExtensions
{
    public static IServiceCollection AddToolServices(this IServiceCollection services)
    {
        services.AddSingleton<WebFetchCache>();

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

        services.AddTool<BashTool>("Bash", nameof(BashTool.ExecuteAsync), ToolRisk.Dynamic,
            aliases: ["shell", "sh", "ps"], concurrency: false,
            searchHint: "execute a shell command (bash or PowerShell via shell parameter)");
        services.AddSingleton<ConversationShellExecutorManager>();
        services.AddSingleton<IShellExecutorCleanup>(sp => sp.GetRequiredService<ConversationShellExecutorManager>());

        services.AddTool<ReadTool>("Read", nameof(ReadTool.ReadAsync), ToolRisk.ReadOnly, searchHint: "read file contents with offset/limit");
        services.AddTool<WriteTool>("Write", nameof(WriteTool.WriteAsync), ToolRisk.Destructive, concurrency: false, searchHint: "create or overwrite a file",
            category: ToolCategory.FileEdit | ToolCategory.FileWrite);
        services.AddTool<EditTool>("Edit", nameof(EditTool.EditAsync), ToolRisk.Destructive, concurrency: false, searchHint: "search-replace edit a file",
            category: ToolCategory.FileEdit | ToolCategory.FileWrite);
        // Delete is deliberately NOT FileEdit/FileWrite: FileEditContract requires the file to
        // exist post-edit, and FileWrite would auto-approve under AcceptEdits — deletion is
        // irreversible and always requires explicit approval (FileDelete feeds FileSystemInvariant).
        services.AddTool<DeleteTool>("Delete", nameof(DeleteTool.DeleteAsync), ToolRisk.Destructive, concurrency: false, searchHint: "delete a file or directory",
            loadPolicy: ToolLoadPolicy.Contextual, keywords: ["delete", "remove"], category: ToolCategory.FileDelete);
        services.AddTool<LsTool>("LS", nameof(LsTool.ListAsync), ToolRisk.ReadOnly, searchHint: "list directory contents");
        services.AddTool<GlobTool>("Glob", nameof(GlobTool.GlobAsync), ToolRisk.ReadOnly, searchHint: "glob pattern file search");
        services.AddTool<GrepTool>("Grep", nameof(GrepTool.SearchAsync), ToolRisk.ReadOnly, searchHint: "regex content search (ripgrep)");
        services.AddTool<FindReferencesTool>("FindReferences", nameof(FindReferencesTool.FindAsync), ToolRisk.ReadOnly, searchHint: "find code references",
            loadPolicy: ToolLoadPolicy.Contextual, keywords: ["reference"]);
        services.AddTool<ApplyWorkspaceEditTool>("ApplyWorkspaceEdit", nameof(ApplyWorkspaceEditTool.ApplyAsync), ToolRisk.Destructive, concurrency: false, searchHint: "apply an LSP workspace edit to files",
            loadPolicy: ToolLoadPolicy.Deferred, keywords: ["workspace edit"], category: ToolCategory.FileWrite);

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
        services.AddTool<EnterWorktreeTool>("EnterWorktree", nameof(EnterWorktreeTool.EnterAsync), ToolRisk.Destructive, concurrency: false, searchHint: "enter or create a git worktree",
            loadPolicy: ToolLoadPolicy.Deferred, keywords: ["worktree"]);
        services.AddTool<ExitWorktreeTool>("ExitWorktree", nameof(ExitWorktreeTool.ExitAsync), ToolRisk.Destructive, concurrency: false, searchHint: "exit a git worktree",
            loadPolicy: ToolLoadPolicy.Deferred, keywords: ["worktree"]);

        // Plan authoring tool is available only inside the Plan capability boundary.
        // SubmitPlan persists the final plan and closes immediately in FinalizingPlanRun;
        // approval is a persisted command. PlanExclusive: excluded from Build runs — the
        // approved-plan Build run must not re-plan via SubmitPlan (ToolCapabilityResolver
        // enforces this boundary).
        services.AddSingleton<OrchestrationEventBus>();
        services.AddTool<CreatePlanTool>("SubmitPlan", nameof(CreatePlanTool.SubmitPlanAsync), ToolRisk.Safe, searchHint: "write and submit the finalized plan for persisted user approval",
            loadPolicy: ToolLoadPolicy.Contextual, keywords: ["plan", "submit", "approve"], category: ToolCategory.PlanAllowed | ToolCategory.PlanExclusive);

        // Approved Build runs must persist structured progress and verification evidence.
        // CompletePlanExecution is auto-derived by the orchestration layer (see PlanExecutionTool)
        // when every step reaches a terminal state — no longer exposed to the LLM.
        services.AddTool<PlanExecutionTool>(PlanToolNames.UpdateStep, nameof(PlanExecutionTool.UpdatePlanStepAsync), ToolRisk.Safe,
            searchHint: "update approved plan step execution status", loadPolicy: ToolLoadPolicy.Always,
            keywords: ["plan", "step", "progress"]);
        services.AddTool<PlanExecutionTool>(PlanToolNames.CompleteVerification, nameof(PlanExecutionTool.CompletePlanVerificationAsync), ToolRisk.Safe,
            searchHint: "persist approved plan verification evidence", loadPolicy: ToolLoadPolicy.Always,
            keywords: ["plan", "verification", "evidence"]);

        // Cron tools (registered via AddCronTools in OneCode.Automation)
        services.AddCronTools();

        services.AddTool<ListMcpResourcesTool>("ListMcpResources", nameof(ListMcpResourcesTool.ListResourcesAsync), ToolRisk.ReadOnly, searchHint: "list MCP resources",
            loadPolicy: ToolLoadPolicy.Contextual, keywords: ["mcp", "resource"]);
        services.AddTool<ReadMcpResourceTool>("ReadMcpResource", nameof(ReadMcpResourceTool.ReadResourceAsync), ToolRisk.ReadOnly, searchHint: "read an MCP resource",
            loadPolicy: ToolLoadPolicy.Contextual, keywords: ["mcp", "resource"]);
        // BrowserFetch：以能力命名的一等浏览器工具（与 WebFetch 对仗），模型感知不到 MCP。
        // 一次调用封装 SSRF 校验 → 按需连接内置 playwright → navigate+snapshot；
        // 连接副作用经 Dynamic 风险 Conditional 审批对用户可见。Always 加载：
        // WebFetch 的降级 hint 必须在同一轮就能落地，不能依赖下一轮的目录装配。
        services.AddTool<BrowserFetchTool>("BrowserFetch", nameof(BrowserFetchTool.FetchAsync), ToolRisk.Dynamic,
            concurrency: false, searchHint: "render a JavaScript-only page in a real headless browser",
            loadPolicy: ToolLoadPolicy.Always);

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
