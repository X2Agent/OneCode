using OneCode.Infrastructure.Middleware;
using OneCode.Infrastructure.Middleware.Contracts;
using OneCode.Infrastructure.Agent.RunMiddleware;
using OneCode.Core.Tokens;
using OneCode.Core.Coordinator;
using OneCode.Core.Domain;
using OneCode.Core.Permissions;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Compaction;
using Microsoft.Extensions.AI;
using OneCode.Core.Hooks;
using OneCode.Core.Tools;

namespace OneCode.Infrastructure.Agent;

public sealed class AgentPipelineMetrics
{
    private int _toolCallCount;

    public int ToolCallCount => Volatile.Read(ref _toolCallCount);

    public int IncrementToolCallCount() => Interlocked.Increment(ref _toolCallCount);
}

public sealed record AgentPipelineHandle(AIAgent Agent, AgentPipelineMetrics Metrics)
{
    /// <summary>
    /// Lifetime owner for the per-run context providers handed to the agent.
    /// </summary>
    /// <remarks>
    /// MAF's <c>ChatClientAgent</c> does not dispose its <c>AIContextProviders</c>. Providers built per
    /// run (skills) hold real resources, so the assembling host sets this and disposes it after the
    /// run's last use — including cancellation, exceptions and early stream exit. Null when the caller
    /// owns the providers (DI singletons) or nothing disposable was built.
    /// </remarks>
    public IDisposable? ContextProviderLease { get; set; }
}

public sealed record ChatClientAgentBuildOptions
{
    public required IChatClient ChatClient { get; init; }
    public required string Name { get; init; }
    public required ChatOptions ChatOptions { get; init; }
    public required ILoggerFactory LoggerFactory { get; init; }
    public required IServiceProvider ServiceProvider { get; init; }
    public required AgentPipelineOptions PipelineOptions { get; init; }

    /// <summary>
    /// Product tool registry used to decide which tools must carry an approval boundary.
    /// When null the approval protocol has no marker source and <see cref="ToolApprovalMarker"/> is a no-op.
    /// </summary>
    public ToolMetadataRegistry? ToolMetadata { get; init; }

    /// <summary>
    /// Product compaction strategy, handed to Harness so it owns the single compaction provider.
    /// When null (AutoDream and other tool-only paths) no in-loop compaction runs.
    /// </summary>
    public CompactionStrategy? CompactionStrategy { get; init; }

    /// <summary>
    /// Shared harness instructions. MAF composes them ahead of the agent instructions carried by
    /// <see cref="ChatOptions"/>. Null leaves MAF's default in place; an empty string suppresses it.
    /// </summary>
    public string? HarnessInstructions { get; init; }

    public IReadOnlyList<AIContextProvider>? AgentContextProviders { get; init; }
}

public sealed record AgentPipelineOptions
{
    public required string WorkingDirectory { get; init; }
    public EditTransaction? EditTransaction { get; init; }
    public Action<FileChange>? FileChangeCallback { get; init; }
    public IPermissionChecker? PermissionChecker { get; init; }
    public PermissionMode PermissionMode { get; init; } = PermissionMode.Default;
    public int MaxToolCalls { get; init; } = 50;
    public string ToolLimitMessage { get; init; } = "Maximum tool call limit reached.";
    public Func<string, bool>? IsToolAllowed { get; init; }
    public IHookExecutionService? HookExecutionService { get; init; }
    public bool EnableEditTransaction { get; init; } = true;
    /// <summary>
    /// When true, installs tool-result size truncation middleware (character budget).
    /// Unrelated to <see cref="MaxToolCalls"/> / Harness MaximumIterationsPerRequest
    /// which limit invocation count or rounds.
    /// </summary>
    public bool EnableToolResultBudget { get; init; } = true;

    // Harness Engineering: safety invariants + state machine (Layer 0 + Layer 2)
    public IReadOnlyList<ISafetyInvariant>? SafetyInvariants { get; init; }
    public bool EnableStateMachine { get; init; } = true;
    public bool EnableSafetyInvariants { get; init; } = true;

    // Harness Engineering: behavior contracts + sequence detection
    public IReadOnlyList<FileEditContract>? BehaviorContracts { get; init; }
    public bool EnableBehaviorContracts { get; init; } = true;
    public bool EnableTaskRecovery { get; init; } = true;

    // Harness Engineering: post-tool verification check (Layer 1)
    // Triggers build/type-check after N source file edits, feeds errors back to LLM.
    // Multi-language: routed by IVerificationProvider.IsSourceFile + VerificationProfile.
    public IVerificationProvider? VerificationProvider { get; init; }
    public bool EnableVerification { get; init; } = false;
    public VerificationOptions? VerificationOptions { get; init; }

    // MAF Harness: ToolApprovalAgent — standard MAF approval flow
    public bool EnableToolApproval { get; init; } = true;

    /// <summary>
    /// When true, Harness mounts its <c>FileMemoryProvider</c> so the agent has session working-memory
    /// tools. Decided per profile: read-only and concurrent paths must not receive a write surface.
    /// </summary>
    public bool EnableFileMemory { get; init; }

    /// <summary>
    /// When true, Harness mounts its <c>TodoProvider</c> so the agent keeps its own per-session
    /// checklist. Distinct from host execution tracking, which the product task service owns.
    /// </summary>
    public bool EnableTodo { get; init; }

    public IEnumerable<Func<ToolAutoApprovalRuleContext, ValueTask<bool>>>? AutoApprovalRules { get; init; }

    // Permission context fields
    // These populate ToolPermissionContext so that PermissionChecker strategies
    // can evaluate rules and validate paths against additional directories.

    /// <summary>User-configured permission rules (allow/deny/ask) keyed by source.</summary>
    public IReadOnlyDictionary<string, PermissionRuleGroup>? RulesBySource { get; init; }

    /// <summary>Additional working directories beyond the main WorkingDirectory.</summary>
    public IReadOnlyDictionary<string, AdditionalWorkingDirectory>? AdditionalWorkingDirectories { get; init; }


    /// <summary>
    /// Optional sink for tool call events. When set, the pipeline emits
    /// OrchestrationEvent.ToolStart/ToolDone for every tool invocation, enabling TEAM mode
    /// to stream tool activity to the TUI in real time.
    /// </summary>
    public Action<OrchestrationEvent>? OrchestrationEventSink { get; init; }

    /// <summary>
    /// 当前模型 ID（如 "claude-sonnet-4"），传递给 ToolResultUnwrapMiddleware 用于
    /// 选择序列化格式（支持结构化 JSON 的模型用 JSON，不支持的降级为 Markdown）。
    /// </summary>
    public string? ModelId { get; init; }

    /// <summary>
    /// 当前 provider ID（如 "anthropic"/"openai"/"ollama"），与 ModelId 一起决定序列化格式。
    /// </summary>
    public string? ProviderId { get; init; }

    /// <summary>
    /// ITokenLedger 实例（可选）。当设置时，UsageTrackingRunMiddleware 会在
    /// Agent Run 级统一拦截 LLM 返回的 Usage 并写入 ITokenLedger，确保所有路径
    /// （流式/非流式/Goal/Team/headless）的 token 用量都被记录，使
    /// <c>--max-budget-tokens</c> 预算熔断在所有路径下生效。
    /// </summary>
    public ITokenLedger? TokenLedger { get; init; }

    /// <summary>
    /// 当前会话 ID。传递给 UsageTrackingRunMiddleware，使 ITokenLedger 的 per-session
    /// 用量能正确记录，TokenUsageTracker 能从 ITokenLedger 读取 session 级 token 计数。
    /// </summary>
    public SessionId? ConversationId { get; init; }

    /// <summary>
    /// 预算上限（token 数，输入 + 输出，可选）。当设置且 <see cref="ITokenLedger"/> 非空时，
    /// BudgetGuardRunMiddleware 会在 Agent Run 级执行 <b>pre-execution</b> 预算检查：
    /// 若 <see cref="ITokenLedger.GetTotalTokens"/> 已达到或超过此值，短路返回错误响应，
    /// 不发起 LLM 调用。null 表示不限制预算（不执行 pre-execution 检查）。
    /// post-execution 的预算状态报告仍由 MainAgentRunner 负责。
    /// </summary>
    public long? MaxBudgetTokens { get; init; }

}

public static class AgentPipelineBuilder
{
    public static AgentPipelineHandle BuildChatClientAgent(ChatClientAgentBuildOptions options)
    {
        var chatClient = options.ChatClient;

        var harnessToolApproval = options.PipelineOptions.EnableToolApproval
            ? new ToolApprovalAgentOptions
            {
                AutoApprovalRules = options.PipelineOptions.AutoApprovalRules
                    ?? AutoApprovalRulesFactory.Create(
                        options.PipelineOptions.PermissionMode,
                        options.PipelineOptions.WorkingDirectory,
                        options.PipelineOptions.RulesBySource,
                        options.PipelineOptions.AdditionalWorkingDirectories,
                        options.PipelineOptions.PermissionChecker),
            }
            : null;

        // The approval boundary is an opt-in marker on each tool, not something the framework infers
        // from a permission decision. Applying it here — the single funnel for Main / forked / Team
        // assembly — keeps one policy source (ToolMetadataRegistry.ApprovalMode) and prevents the
        // paths from drifting. Skipped when this path has no approval machinery, because an
        // unresolvable approval request would otherwise block every call in the batch.
        var markedTools = options.PipelineOptions.EnableToolApproval
            ? ToolApprovalMarker.Apply(options.ChatOptions.Tools, options.ToolMetadata)
            : options.ChatOptions.Tools;

        var chatOptions = options.ChatOptions.Clone();
        chatOptions.Tools = markedTools;

        var agentOptions = new HarnessAgentOptions
        {
            Name = options.Name,
            ChatOptions = chatOptions,
            AIContextProviders = options.AgentContextProviders,
            // W5-B: MAF core package (1.21.0) still ships InMemory only — official file-backed
            // ChatHistoryProviders (CosmosNoSql / Valkey) live in separate packages.
            // Interactive multi-turn history is Session transcript → Messages; InMemory is
            // cleared after mafSession restore when Messages are present (see AgentSessionStore W5-A).
            ChatHistoryProvider = new InMemoryChatHistoryProvider(),
            ToolApprovalAgentOptions = harnessToolApproval,
            DisableToolAutoApproval = !options.PipelineOptions.EnableToolApproval,
            // P1: same numeric source as middleware MaxToolCalls (iteration rounds ≈ tool-call budget).
            MaximumIterationsPerRequest = options.PipelineOptions.MaxToolCalls,

            // Harness owns the composition order: harness fragment first, then the agent body.
            HarnessInstructions = options.HarnessInstructions,

            // Compaction has exactly one owner: Harness. The product builds the strategy (its ratios,
            // formatter, summary guard and prompt) and Harness installs the provider in its own
            // pipeline position. Mounting a second provider from the product side would compact the
            // same request twice and shadow this one's session state.
            CompactionStrategy = options.CompactionStrategy,

            // Per-profile opt-in. Harness defaults this to enabled, so the value must be stated
            // explicitly in both directions rather than only turned off.
            DisableFileMemory = !options.PipelineOptions.EnableFileMemory,
            DisableTodoProvider = !options.PipelineOptions.EnableTodo,
        };

        // Bind working memory to the session's project instead of the Harness default, which roots it
        // at the process directory and would cross project boundaries on /cd.
        if (options.PipelineOptions.EnableFileMemory)
        {
            agentOptions.FileMemoryStore = FileMemoryStorePaths.CreateStore(options.PipelineOptions.WorkingDirectory);
        }

        OneCodeHarnessDefaults.ApplyProductOptOuts(agentOptions);

        var agent = new HarnessAgent(
            chatClient,
            agentOptions,
            options.LoggerFactory,
            options.ServiceProvider);

        return Build(agent, options.PipelineOptions, options.LoggerFactory, options.ServiceProvider, harnessToolApproval is not null);
    }

    public static AgentPipelineHandle Build(
        AIAgent agent,
        AgentPipelineOptions options,
        ILoggerFactory loggerFactory,
        IServiceProvider serviceProvider,
        bool harnessOwnsToolApproval = false)
    {
        var metrics = new AgentPipelineMetrics();

        var builder = agent.AsBuilder();

        // Agent Run 级中间件（最外层）：BudgetGuard 预算守卫
        // pre-execution 检查：若 ITokenLedger 累计 token 已达 MaxBudgetTokens，短路返回错误响应，
        // 不发起 LLM 调用，防止失控后继续消耗。位于 UsageTracking 外层，确保在任何
        // LLM 调用前拦截；短路时不产生 Usage，UsageTracking 内层不会被调用。
        if (options.TokenLedger is not null && options.MaxBudgetTokens is not null)
        {
            var (guardRun, guardStream) = BudgetGuardRunMiddleware.Create(
                options.TokenLedger,
                options.MaxBudgetTokens,
                loggerFactory.CreateLogger("BudgetGuardRunMiddleware"));
            builder = builder.Use(guardRun, guardStream);
        }

        // Agent Run 级中间件（次外层）：Usage 追踪
        // 统一拦截所有 agent run（流式/非流式/Goal/Team/headless）的 LLM Usage，
        // 写入 ITokenLedger，确保 --max-budget-tokens 预算熔断在所有路径下生效。
        // 放在 BudgetGuard 内层：BudgetGuard 放行后，本层记录本次 run 的实际 usage；
        // 下一次 run 时 BudgetGuard 读取更新后的累计 token 进行检查。
        if (options.TokenLedger is not null)
        {
            var (runFunc, runStreamingFunc) = UsageTrackingRunMiddleware.Create(
                options.TokenLedger,
                options.ModelId,
                loggerFactory.CreateLogger("UsageTrackingRunMiddleware"),
                options.ConversationId);
            builder = builder.Use(runFunc, runStreamingFunc);
        }

        // Agent Run 级中间件（最内层 Run 中间件）：PromptTooLong 恢复
        // MAF 最佳实践：PromptTooLong 是模型推理阶段的异常，应在 Run middleware 层拦截，
        // 而非在 Runner 层重建 pipeline。中间件包裹 innerAgent.RunAsync，catch 异常后
        // fire hooks + 截断消息历史 + 重试。注册为 agent-level（对所有 run 生效），
        // Main/Worker/Team/Goal 路径自动获得恢复能力。
        //
        // 层次顺序：BudgetGuard → UsageTracking → PromptTooLongRecovery → [function calling]
        // PromptTooLongRecovery 在 UsageTracking 内层：retry 时只有最终成功的 response
        // 流经 UsageTracking（记录 usage）；失败的 attempt（PromptTooLong 不消耗 token）
        // 被 PromptTooLongRecovery 内部捕获，不影响 UsageTracking。
        {
            var (ptlRun, ptlStream) = PromptTooLongRecoveryRunMiddleware.Create(
                options.HookExecutionService,
                loggerFactory.CreateLogger("PromptTooLongRecoveryRunMiddleware"));
            builder = builder.Use(ptlRun, ptlStream);
        }

        // Tool middleware install order (source order == MAF AsBuilder.Use order).
        // Optional stages are skipped when disabled; relative order of enabled stages stays fixed:
        // SafetyInvariant → Hook → ToolCallEvent → PermissionAndLimit → StateMachine →
        // EditTransaction → EditGuard(contract pre + verification) → ResultBudget → ResultUnwrap → ToolApproval.
        // Custom behavior belongs in these middleware / MAF extension points — not a Stage registry.

        if (options.EnableSafetyInvariants)
        {
            var invariants = options.SafetyInvariants
                ?? OneCodeToolMiddleware.CreateDefaultSafetyInvariants(options.WorkingDirectory);
            builder = builder.Use(
                SafetyInvariantMiddleware.Create(
                    invariants, loggerFactory.CreateLogger("SafetyInvariantMiddleware")));
        }

        if (options.HookExecutionService is not null)
        {
            builder = builder.Use(
                HookMiddleware.Create(options, loggerFactory.CreateLogger("HookMiddleware")));
        }

        if (options.OrchestrationEventSink is not null)
        {
            builder = builder.Use(
                ToolCallEventMiddleware.Create(
                    options, loggerFactory.CreateLogger("ToolCallEventMiddleware")));
        }

        builder = builder.Use(PermissionAndLimitMiddleware.Create(options, metrics));

        if (options.EnableStateMachine)
        {
            builder = builder.Use(
                StateMachineMiddleware.Create(
                    loggerFactory.CreateLogger("StateMachineMiddleware"),
                    enableStrikeGuidance: options.EnableTaskRecovery));
        }

        if (options.EnableEditTransaction && options.EditTransaction is not null)
        {
            builder = builder.Use(
                new EditTransactionMiddleware(
                    options.EditTransaction,
                    options.WorkingDirectory,
                    options.FileChangeCallback,
                    loggerFactory.CreateLogger<EditTransactionMiddleware>()).CreateDelegate());
        }

        // W2-B: one EditGuard middleware (contract pre + optional verification post).
        var editContracts = options.EnableBehaviorContracts ? options.BehaviorContracts : null;
        var editVerification = options.EnableVerification ? options.VerificationProvider : null;
        if (editContracts is { Count: > 0 } || editVerification is not null)
        {
            builder = builder.Use(
                EditGuardMiddleware.Create(
                    editContracts,
                    editVerification,
                    options.WorkingDirectory,
                    options.VerificationOptions ?? Middleware.VerificationOptions.Default,
                    loggerFactory.CreateLogger("EditGuardMiddleware")));
        }

        if (options.EnableToolResultBudget)
        {
            builder = builder.Use(
                new ToolExecutionBudgetMiddleware(
                    logger: loggerFactory.CreateLogger<ToolExecutionBudgetMiddleware>()).CreateDelegate());
        }

        builder = builder.Use(
            new ToolResultUnwrapMiddleware(
                modelId: options.ModelId,
                providerId: options.ProviderId,
                logger: loggerFactory.CreateLogger<ToolResultUnwrapMiddleware>()).CreateDelegate());

        if (options.EnableToolApproval && !harnessOwnsToolApproval)
        {
            var rules = options.AutoApprovalRules
                ?? AutoApprovalRulesFactory.Create(
                    options.PermissionMode,
                    options.WorkingDirectory,
                    options.RulesBySource,
                    options.AdditionalWorkingDirectories,
                    options.PermissionChecker);
            // Prefer assignment so approval wraps the pipeline (MAF builder chaining).
            builder = builder.UseToolApproval(
                new ToolApprovalAgentOptions
                {
                    AutoApprovalRules = rules,
                });
        }

        return new AgentPipelineHandle(builder.Build(serviceProvider), metrics);
    }
}

