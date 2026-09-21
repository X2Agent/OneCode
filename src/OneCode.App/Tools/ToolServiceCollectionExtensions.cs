using Microsoft.Extensions.AI;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using OneCode.App.Query;
using OneCode.App.Services;
using OneCode.App.Services.Streaming;
using OneCode.Automation;
using OneCode.Core.Exec;
using OneCode.Core.Mcp;

namespace OneCode.App.Tools;

/// <summary>
/// 工具目录与工具注册流 DI 注册——与工具实现（ToolCatalog / 各 Tool）同目录维护。
/// <c>AddToolInstance</c> 流的调用顺序即 <see cref="ToolRegistration"/> 的 IEnumerable 注入顺序，
/// 重排会改变工具目录装配结果——除非有意变更，否则保持现有顺序。
/// 由组合根 <see cref="OneCode.App.OneCodeApp"/> 显式调用。
/// </summary>
public static class ToolServiceCollectionExtensions
{
    public static IServiceCollection AddToolServices(this IServiceCollection services)
    {
        services.AddSingleton<IMemoryCache>(_ => new MemoryCache(new MemoryCacheOptions
        {
            SizeLimit = 50 * 1024 * 1024,
        }));

        services.AddSingleton<ToolMetadataRegistry>();
        services.AddSingleton<ToolCatalog>(sp =>
        {
            var registrations = sp.GetServices<ToolRegistration>().ToList();
            var metadata = sp.GetRequiredService<ToolMetadataRegistry>();
            var mcp = sp.GetService<IMcpConnectionManager>();
            var staticTools = new Lazy<List<AIFunction>>(
                () => ToolCatalog.BuildStaticTools(sp, registrations, metadata),
                LazyThreadSafetyMode.PublicationOnly);
            return new ToolCatalog(staticTools, metadata, mcp);
        });
        services.AddSingleton<IToolCatalog>(sp => sp.GetRequiredService<ToolCatalog>());
        services.AddSingleton<IToolCapabilityResolver, ToolCapabilityResolver>();

        services.AddSingleton<IShellExecutor, OneCodeShellExecutor>();
        services.AddToolInstance("Bash", (BashTool tool) => AIFunctionFactory.Create(tool.ExecuteAsync, name: "Bash"), ToolRisk.Dynamic,
            aliases: ["shell", "sh", "ps"], concurrency: false,
            searchHint: "execute a shell command (bash or PowerShell via shell parameter)");
        services.AddSingleton<ConversationShellExecutorManager>();
        services.AddSingleton<IShellExecutorCleanup>(sp => sp.GetRequiredService<ConversationShellExecutorManager>());

        services.AddToolInstance("Read", (ReadTool tool) => AIFunctionFactory.Create(tool.ReadAsync, name: "Read"), ToolRisk.ReadOnly, searchHint: "read file contents with offset/limit");
        services.AddToolInstance("Write", (WriteTool tool) => AIFunctionFactory.Create(tool.WriteAsync, name: "Write"), ToolRisk.Destructive, concurrency: false, searchHint: "create or overwrite a file",
            category: ToolCategory.FileEdit | ToolCategory.FileWrite);
        services.AddToolInstance("Edit", (EditTool tool) => AIFunctionFactory.Create(tool.EditAsync, name: "Edit"), ToolRisk.Destructive, concurrency: false, searchHint: "search-replace edit a file",
            category: ToolCategory.FileEdit | ToolCategory.FileWrite);
        // Delete is deliberately NOT FileEdit/FileWrite: FileEditContract requires the file to
        // exist post-edit, and FileWrite would auto-approve under AcceptEdits — deletion is
        // irreversible and always requires explicit approval (FileDelete feeds FileSystemInvariant).
        services.AddToolInstance("Delete", (DeleteTool tool) => AIFunctionFactory.Create(tool.DeleteAsync, name: "Delete"), ToolRisk.Destructive, concurrency: false, searchHint: "delete a file or directory",
            loadPolicy: ToolLoadPolicy.Contextual, keywords: ["delete", "remove"], category: ToolCategory.FileDelete);
        services.AddToolInstance("LS", (LsTool tool) => AIFunctionFactory.Create(tool.ListAsync, name: "LS"), ToolRisk.ReadOnly, searchHint: "list directory contents");
        services.AddToolInstance("Glob", (GlobTool tool) => AIFunctionFactory.Create(tool.GlobAsync, name: "Glob"), ToolRisk.ReadOnly, searchHint: "glob pattern file search");
        services.AddToolInstance("Grep", (GrepTool tool) => AIFunctionFactory.Create(tool.SearchAsync, name: "Grep"), ToolRisk.ReadOnly, searchHint: "regex content search (ripgrep)");
        services.AddToolInstance("FindReferences", (FindReferencesTool tool) => AIFunctionFactory.Create(tool.FindAsync, name: "FindReferences"), ToolRisk.ReadOnly, searchHint: "find code references",
            loadPolicy: ToolLoadPolicy.Contextual, keywords: ["reference"]);
        services.AddToolInstance("ApplyWorkspaceEdit", (ApplyWorkspaceEditTool tool) => AIFunctionFactory.Create(tool.ApplyAsync, name: "ApplyWorkspaceEdit"), ToolRisk.Destructive, concurrency: false, searchHint: "apply an LSP workspace edit to files",
            loadPolicy: ToolLoadPolicy.Deferred, keywords: ["workspace edit"], category: ToolCategory.FileWrite);

        services.AddToolInstance("WebFetch", (WebFetchTool tool) => AIFunctionFactory.Create(tool.FetchAsync, name: "WebFetch"), ToolRisk.ReadOnly, searchHint: "fetch web page content as markdown",
            loadPolicy: ToolLoadPolicy.Contextual, keywords: ["web fetch", "fetch web", "url"]);
        services.AddToolInstance("WebSearch", (WebSearchTool tool) => AIFunctionFactory.Create(tool.SearchAsync, name: "WebSearch"), ToolRisk.ReadOnly, searchHint: "search the web",
            loadPolicy: ToolLoadPolicy.Contextual, keywords: ["web search", "search web"]);

        services.AddToolInstance("Task", (TaskTool tool) => AIFunctionFactory.Create(tool.ExecuteAsync, name: "Task"), ToolRisk.Safe, searchHint: "inspect and control host background tasks (get/list/stop/output)",
            loadPolicy: ToolLoadPolicy.Contextual, keywords: ["task", "background task"], category: ToolCategory.PlanAllowed);
        services.AddToolInstance("BackgroundRun", (BackgroundRunTool tool) => AIFunctionFactory.Create(tool.RunAsync, name: "BackgroundRun"), ToolRisk.Destructive, concurrency: false, searchHint: "run command in background",
            loadPolicy: ToolLoadPolicy.Contextual, keywords: ["background"]);
        services.AddToolInstance("BackgroundWait", (BackgroundWaitTool tool) => AIFunctionFactory.Create(tool.WaitAsync, name: "BackgroundWait"), ToolRisk.ReadOnly, searchHint: "wait for background task",
            loadPolicy: ToolLoadPolicy.Contextual, keywords: ["background"]);

        // AgentTool resolves via normal DI (ICacheSafeParamsProvider) — no ChatService locator.
        services.AddToolInstance("Agent", (AgentTool tool) => AIFunctionFactory.Create(tool.RunAgentAsync, name: "Agent"), ToolRisk.Safe, searchHint: "run a sub-agent on a delegated task",
            loadPolicy: ToolLoadPolicy.Contextual, keywords: ["agent", "sub-agent", "delegate"], category: ToolCategory.PlanAllowed);
        services.AddToolInstance("ParallelAgents", (ParallelAgentsTool tool) => AIFunctionFactory.Create(tool.RunParallelAsync, name: "ParallelAgents"), ToolRisk.Safe, searchHint: "run sub-agents with DAG dependencies",
            loadPolicy: ToolLoadPolicy.Contextual, keywords: ["agent", "parallel"], category: ToolCategory.PlanAllowed);

        services.AddSingleton<TuiInteractionBridge>();
        services.AddSingleton<IUserQuestionService, UserQuestionService>();
        services.AddSingleton<IClarificationInteractionService, ClarificationInteractionService>();

        services.AddSingleton<AskUserQuestionTool>();
        services.AddToolInstance("AskUserQuestion", (AskUserQuestionTool tool) => AIFunctionFactory.Create(tool.AskAsync, name: "AskUserQuestion"), ToolRisk.ReadOnly, searchHint: "ask the user one blocking question",
            category: ToolCategory.PlanAllowed);
        services.AddToolInstance("AskUserQuestions", (AskUserQuestionTool tool) => AIFunctionFactory.Create(tool.AskMultipleAsync, name: "AskUserQuestions"), ToolRisk.ReadOnly, searchHint: "ask the user multiple related questions in one wizard",
            category: ToolCategory.PlanAllowed);

        services.AddToolInstance("SymbolSearch", (SymbolSearchTool tool) => AIFunctionFactory.Create(tool.SymbolSearchAsync, name: "SymbolSearch"), ToolRisk.ReadOnly, searchHint: "search code symbols",
            loadPolicy: ToolLoadPolicy.Contextual, keywords: ["symbol"]);
        services.AddToolInstance("Lsp", (LspTool tool) => AIFunctionFactory.Create(tool.ExecuteLspAsync, name: "Lsp"), ToolRisk.ReadOnly, searchHint: "perform language-server operations",
            loadPolicy: ToolLoadPolicy.Contextual, keywords: ["lsp", "language server"]);
        services.AddToolInstance("EnterWorktree", (EnterWorktreeTool tool) => AIFunctionFactory.Create(tool.EnterAsync, name: "EnterWorktree"), ToolRisk.Destructive, concurrency: false, searchHint: "enter or create a git worktree",
            loadPolicy: ToolLoadPolicy.Deferred, keywords: ["worktree"]);
        services.AddToolInstance("ExitWorktree", (ExitWorktreeTool tool) => AIFunctionFactory.Create(tool.ExitAsync, name: "ExitWorktree"), ToolRisk.Destructive, concurrency: false, searchHint: "exit a git worktree",
            loadPolicy: ToolLoadPolicy.Deferred, keywords: ["worktree"]);

        // Plan authoring tool is available only inside the Plan capability boundary.
        // SubmitPlan persists the final plan and closes immediately in FinalizingPlanRun;
        // approval is a persisted command. PlanExclusive: excluded from Build runs — the
        // approved-plan Build run must not re-plan via SubmitPlan (ToolCapabilityResolver
        // enforces this boundary).
        services.AddSingleton<OrchestrationEventBus>();
        services.AddToolInstance("SubmitPlan", (CreatePlanTool tool) => AIFunctionFactory.Create(tool.SubmitPlanAsync, name: "SubmitPlan"), ToolRisk.Safe, searchHint: "write and submit the finalized plan for persisted user approval",
            loadPolicy: ToolLoadPolicy.Contextual, keywords: ["plan", "submit", "approve"], category: ToolCategory.PlanAllowed | ToolCategory.PlanExclusive);

        // Approved Build runs must persist structured progress and verification evidence.
        // CompletePlanExecution is auto-derived by the orchestration layer (see PlanExecutionTool)
        // when every step reaches a terminal state — no longer exposed to the LLM.
        services.AddToolInstance(PlanToolNames.UpdateStep, (PlanExecutionTool tool) => AIFunctionFactory.Create(tool.UpdatePlanStepAsync, name: PlanToolNames.UpdateStep), ToolRisk.Safe,
            searchHint: "update approved plan step execution status", loadPolicy: ToolLoadPolicy.Always,
            keywords: ["plan", "step", "progress"]);
        services.AddToolInstance(PlanToolNames.CompleteVerification, (PlanExecutionTool tool) => AIFunctionFactory.Create(tool.CompletePlanVerificationAsync, name: PlanToolNames.CompleteVerification), ToolRisk.Safe,
            searchHint: "persist approved plan verification evidence", loadPolicy: ToolLoadPolicy.Always,
            keywords: ["plan", "verification", "evidence"]);

        // Cron tools (registered via AddCronTools in OneCode.Automation)
        services.AddCronTools();

        services.AddToolInstance("ListMcpResources", (ListMcpResourcesTool tool) => AIFunctionFactory.Create(tool.ListResourcesAsync, name: "ListMcpResources"), ToolRisk.ReadOnly, searchHint: "list MCP resources",
            loadPolicy: ToolLoadPolicy.Contextual, keywords: ["mcp", "resource"]);
        services.AddToolInstance("ReadMcpResource", (ReadMcpResourceTool tool) => AIFunctionFactory.Create(tool.ReadResourceAsync, name: "ReadMcpResource"), ToolRisk.ReadOnly, searchHint: "read an MCP resource",
            loadPolicy: ToolLoadPolicy.Contextual, keywords: ["mcp", "resource"]);
        // BrowserFetch：以能力命名的一等浏览器工具（与 WebFetch 对仗），模型感知不到 MCP。
        // 一次调用封装 SSRF 校验 → 按需连接内置 playwright → navigate+snapshot；
        // 连接副作用经 Dynamic 风险 Conditional 审批对用户可见。Always 加载：
        // WebFetch 的降级 hint 必须在同一轮就能落地，不能依赖下一轮的目录装配。
        services.AddToolInstance("BrowserFetch", (BrowserFetchTool tool) => AIFunctionFactory.Create(tool.FetchAsync, name: "BrowserFetch"), ToolRisk.Dynamic,
            concurrency: false, searchHint: "render a JavaScript-only page in a real headless browser",
            loadPolicy: ToolLoadPolicy.Always);

        // ToolSearch (needs runtime metadata access)
        services.AddToolInstance<ToolSearchTool>("ToolSearch",
            tool => AIFunctionFactory.Create(tool.Search, name: "ToolSearch"),
            ToolRisk.ReadOnly, visible: true, searchHint: "search available tools by keyword to discover non-core tools");

        return services;
    }
}
