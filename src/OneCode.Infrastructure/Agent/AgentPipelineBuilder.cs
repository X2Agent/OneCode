#pragma warning disable MAAI001 // MAF 实验性 API（压缩策略 / FileMemoryStore）：升级 MAF 时按升级复查清单回归
using OneCode.Infrastructure.Middleware;
using OneCode.Infrastructure.Agent.RunMiddleware;
using OneCode.Infrastructure.Ai;
using OneCode.Core.Tokens;
using OneCode.Core.Coordinator;
using OneCode.Core.Domain;
using OneCode.Core.Goals;
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
    private int _externalWebToolCallCount;
    private int _modelCallCount;

    public int ToolCallCount => Volatile.Read(ref _toolCallCount);

    public int ExternalWebToolCallCount => Volatile.Read(ref _externalWebToolCallCount);

    /// <summary>真实模型往返次数（Harness 每次迭代一次）；子代理"轮数"的权威口径。</summary>
    public int ModelCallCount => Volatile.Read(ref _modelCallCount);

    public int IncrementToolCallCount() => Interlocked.Increment(ref _toolCallCount);

    public int IncrementExternalWebToolCallCount() => Interlocked.Increment(ref _externalWebToolCallCount);

    public int IncrementModelCallCount() => Interlocked.Increment(ref _modelCallCount);
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

    /// <summary>
    /// MAF chat history contract. Null keeps the framework default
    /// (<c>InMemoryChatHistoryProvider</c>), which is correct for single-turn paths
    /// (Forked agents, Goal sub-goals).
    /// The interactive Main path supplies <c>TranscriptChatHistoryProvider</c> so multi-turn
    /// history flows through the framework contract instead of host-injected request messages.
    /// </summary>
    public Microsoft.Agents.AI.ChatHistoryProvider? ChatHistoryProvider { get; init; }
}

public sealed record AgentPipelineOptions
{
    public required string WorkingDirectory { get; init; }
    public EditTransaction? EditTransaction { get; init; }
    public Action<FileChange>? FileChangeCallback { get; init; }
    public IPermissionChecker? PermissionChecker { get; init; }
    public PermissionMode PermissionMode { get; init; } = PermissionMode.Default;
    public int MaxToolCalls { get; init; } = 50;
    public string ToolLimitMessage { get; init; } = "Tool call budget reached. Stop calling tools and summarize completed work and remaining items.";
    /// <summary>
    /// 外部网络检索工具（<c>WebSearch</c> / <c>WebFetch</c>）单次运行的调用次数上限；
    /// 达到上限后仅拒绝继续调用外部网络工具并提示模型用已有信息继续编码，不封锁 <c>Read</c>/<c>Write</c>/<c>Edit</c>/<c>Bash</c> 等本地工具。
    /// null 表示不单独限制。
    /// </summary>
    public int? MaxExternalWebToolCalls { get; init; }
    /// <summary>
    /// 轮数上限（模型往返次数）。设置时 Harness 迭代上限 = 该值 + 1（预留一轮收尾——
    /// 工具预算触发后模型至少还有一次无工具迭代产出总结）。null = 沿用 MaxToolCalls
    /// 作为迭代上限（主路径既有语义）。
    /// </summary>
    public int? MaxTurns { get; init; }
    /// <summary>
    /// 轮次预算收尾提醒（依赖 <see cref="MaxTurns"/>）：模型调用达到上限的 80% 时
    /// 注入一次收尾指令。只用于单次 run 生命周期的子代理管道——主会话管道跨多条
    /// 用户消息长存，逐调用计数会失真。
    /// </summary>
    public bool EnableTurnBudgetNudge { get; init; }

    /// <summary>
    /// 待办停滞提醒：自上次待办变更累计多次工具调用仍无进展时注入一次催办。
    /// 只用于计划执行 run（待办即步骤进度），见 <c>TodoStallNudgeChatClient</c>。
    /// </summary>
    public bool EnableTodoStallNudge { get; init; }

    /// <summary>
    /// run 封条探针：返回 true 时工具中间件拒绝本 run 的一切后续工具调用并回复
    /// <see cref="RunSealedMessage"/>。用于「交付物已提交，只允许纯文本收尾」的 run（Plan 提交计划后）。
    /// </summary>
    public Func<bool>? IsRunSealed { get; init; }

    /// <summary>封条落下后返回给模型的错误文本。</summary>
    public string RunSealedMessage { get; init; } = "This run is sealed: no more tool calls are accepted. Reply with a short text summary and stop.";

    public Func<string, bool>? IsToolAllowed { get; init; }
    public IHookExecutionService? HookExecutionService { get; init; }

    // Harness Engineering: safety invariants + state machine (Layer 0 + Layer 2)
    public bool EnableStateMachine { get; init; } = true;

    // Harness Engineering: edit pre-checks
    public bool EnableEditPreChecks { get; init; } = true;

    // Harness Engineering: post-tool verification check (Layer 1)
    // Triggers build/type-check after N source file edits, feeds errors back to LLM.
    // Multi-language: routed by IVerificationProvider.IsSourceFile + VerificationProfile.
    public IVerificationProvider? VerificationProvider { get; init; }
    public bool EnableVerification { get; init; } = false;

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
    /// OrchestrationEvent.ToolStart/ToolDone for every tool invocation, enabling multi-agent
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
    /// （流式/非流式/Goal/headless）的 token 用量都被记录，使
    /// <c>--max-budget-tokens</c> 预算熔断在所有路径下生效。
    /// </summary>
    public ITokenLedger? TokenLedger { get; init; }

    /// <summary>
    /// 当前会话 ID。传递给 UsageTrackingRunMiddleware，使 ITokenLedger 的 per-session
    /// 用量能正确记录，TokenUsageTracker 能从 ITokenLedger 读取 session 级 token 计数。
    /// </summary>
    public SessionId? ConversationId { get; init; }

    /// <summary>
    /// 预算上限（token 数，可计费口径，可选）。当设置且 <see cref="ITokenLedger"/> 非空时，
    /// BudgetGuardRunMiddleware 会在 Agent Run 级执行 <b>pre-execution</b> 预算检查：
    /// 若 <see cref="ITokenLedger.GetTotalTokens"/> 已达到或超过此值，短路返回错误响应，
    /// 不发起 LLM 调用。null 表示不限制预算（不执行 pre-execution 检查）。
    /// post-execution 的预算状态报告仍由 MainAgentRunner 负责。
    /// </summary>
    public long? MaxBudgetTokens { get; init; }

    /// <summary>
    /// 预算基线（run 启动时对 ledger 的快照，默认 0 = 会话级累计口径）。
    /// BudgetGuard 只对 <c>GetTotalTokens() - 基线</c> 的增量计预算；BUILD 模式主 run
    /// 传快照值，使 Plan 阶段的调研消耗不占用计划执行的预算。
    /// </summary>
    public long BudgetBaselineTokens { get; init; }

    /// <summary>
    /// 预算 100% 消耗时的用户询问回调（交互模式），经 <see cref="OneCode.Core.Goals.IBudgetDecisionService"/>
    /// 端口取得决策。参数：(超出基线的已消耗量, 预算上限, 取消令牌)，
    /// 返回 ContinueWithExtraBudget=追加预算继续 / WrapUpAndStop=收尾总结 / 其余=直接停止。
    /// null（无人值守路径）时 BudgetGuard 硬停短路。
    /// </summary>
    public Func<long, long, CancellationToken, Task<OneCode.Core.Goals.GoalBudgetDecision>>? OnBudgetExceeded { get; init; }

    /// <summary>预算 70%/90% 预警回调（参数：级别、超出基线的已消耗量、预算上限）。</summary>
    public Action<GoalBudgetWarningLevel, long, long>? OnBudgetWarning { get; init; }

}

public static class AgentPipelineBuilder
{
    public static AgentPipelineHandle BuildHarnessAgent(ChatClientAgentBuildOptions options)
    {
        // 模型拦截点装饰器必须包在 Harness 之外侧、FunctionInvokingChatClient 之下侧：
        // pre/post_model_call 在模型调用边界做纯审计（不支持 deny），流式交付零缓冲，
        // 无匹配 hook 时仅透传（HasActiveHooks 守卫，不构造 payload）。
        var chatClient = options.PipelineOptions.HookExecutionService is not null
            ? new ModelCallHookDecorator(
                options.ChatClient,
                options.PipelineOptions.HookExecutionService,
                options.PipelineOptions.WorkingDirectory,
                options.PipelineOptions.ConversationId?.ToString())
            : options.ChatClient;

        // 轮次预算收尾提醒（子代理管道）：达到 MaxTurns 的 80% 时注入一次收尾指令，
        // 同时把每次模型往返记入 metrics（子代理轮数的权威口径）。
        // 包在 Hook 装饰器外侧——审计看到的就是模型实际收到的（含提醒）请求。
        // 单次 run 内逐模型调用计数，装饰器随管道实例新建，见 TurnBudgetNudgeChatClient。
        var metrics = new AgentPipelineMetrics();
        if (options.PipelineOptions.EnableTurnBudgetNudge && options.PipelineOptions.MaxTurns is { } nudgeMaxTurns)
            chatClient = new TurnBudgetNudgeChatClient(chatClient, nudgeMaxTurns, metrics);

        // 待办停滞提醒（计划执行 run）：逐模型请求观察「自上次勾选以来的工具调用数」。
        if (options.PipelineOptions.EnableTodoStallNudge && options.PipelineOptions.EnableTodo)
            chatClient = new TodoStallNudgeChatClient(chatClient);

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
        // from a permission decision. Applying it here keeps one policy source
        // (ToolMetadataRegistry.ApprovalMode) across Main / forked assembly. Tools that providers
        // inject afterwards (Harness todo list / working memory) are not in this list yet — they are
        // marked per request by ToolApprovalMarkingContextProvider. Skipped when this path has no
        // approval machinery, because an unresolvable approval request would otherwise block every
        // call in the batch.
        var markedTools = options.PipelineOptions.EnableToolApproval
            ? ToolApprovalMarker.Apply(options.ChatOptions.Tools, options.ToolMetadata)
            : options.ChatOptions.Tools;

        // Declare the provider-injected tools in the product registry whenever their providers are
        // mounted, so their approval boundary is an explicit decision rather than the fallback
        // "unregistered name ⇒ Destructive/Always" accidentally being right.
        if (options.ToolMetadata is { } toolMetadata)
        {
            HarnessProviderTools.RegisterMetadata(
                toolMetadata,
                options.PipelineOptions.EnableTodo,
                options.PipelineOptions.EnableFileMemory);
        }

        var chatOptions = options.ChatOptions.Clone();
        chatOptions.Tools = markedTools;

        var agentOptions = new HarnessAgentOptions
        {
            Name = options.Name,
            ChatOptions = chatOptions,
            AIContextProviders = BuildContextProviders(options),
            // Main hands in TranscriptChatHistoryProvider (session transcript bridge);
            // single-turn paths (forked / Goal sub-goals) pass null so HarnessAgent natively
            // creates InMemoryChatHistoryProvider(compactionStrategy.AsChatReducer()).
            ChatHistoryProvider = options.ChatHistoryProvider,
            ToolApprovalAgentOptions = harnessToolApproval,
            DisableToolAutoApproval = !options.PipelineOptions.EnableToolApproval,
            // 轮数上限 + 1：预留一轮收尾（工具预算触发后模型至少还有一次无工具迭代可产出总结）。
            // MaxTurns 未设置时沿用 MaxToolCalls 作迭代上限——主路径（轮=调用同值）行为不变。
            MaximumIterationsPerRequest = options.PipelineOptions.MaxTurns is { } maxTurns
                ? maxTurns + 1
                : options.PipelineOptions.MaxToolCalls,

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

        return Build(agent, options.PipelineOptions, metrics, options.LoggerFactory, options.ServiceProvider);
    }

    /// <summary>
    /// Builds the context provider list handed to Harness, appending the runtime approval boundary last.
    /// </summary>
    /// <remarks>
    /// Order is the contract here: Harness appends this list after its own providers (todo list / working
    /// memory / skills), and <c>ChatClientAgent</c> threads the accumulated context through each provider in
    /// order, letting a provider replace the tool list. Only the final provider sees the tools Harness
    /// injected, which is exactly what the approval marker needs. The appended provider is stateless and
    /// holds nothing disposable, so it needs no tracking in the pipeline handle.
    /// </remarks>
    private static IReadOnlyList<AIContextProvider>? BuildContextProviders(ChatClientAgentBuildOptions options)
    {
        if (!options.PipelineOptions.EnableToolApproval || options.ToolMetadata is not { } metadata)
            return options.AgentContextProviders;

        IReadOnlyList<AIContextProvider> existing = options.AgentContextProviders ?? [];
        return [.. existing, new ToolApprovalMarkingContextProvider(metadata)];
    }

    /// <summary>
    /// Mounts the product middleware stack on a constructed <see cref="HarnessAgent"/> and builds the
    /// runnable agent. Internal: the only entry point is <see cref="BuildHarnessAgent"/>, which always
    /// hands tool approval to Harness (via <see cref="HarnessAgentOptions.ToolApprovalAgentOptions"/>),
    /// so no fallback approval boundary is installed here.
    /// </summary>
    private static AgentPipelineHandle Build(
        AIAgent agent,
        AgentPipelineOptions options,
        AgentPipelineMetrics metrics,
        ILoggerFactory loggerFactory,
        IServiceProvider serviceProvider)
    {
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
                loggerFactory.CreateLogger("BudgetGuardRunMiddleware"),
                options.BudgetBaselineTokens,
                options.OnBudgetExceeded,
                options.OnBudgetWarning);
            builder = builder.Use(guardRun, guardStream);
        }

        // Agent Run 级中间件（次外层）：Usage 追踪
        // 统一拦截所有 agent run（流式/非流式/Goal/headless）的 LLM Usage，
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

        // 工具级中间件装配顺序说明：
        // MAF AIAgentBuilder.Build() 逆序遍历 _agentFactories（从 Count - 1 到 0）。
        // 对于 functionInvocationCallback，后调用 .Use() 的中间件最先挂载到 FunctionInvokingChatClient，
        // 在运行时处于最外层（先进入、后退出）；先调用 .Use() 的中间件处于最内层（紧贴 AIFunction.InvokeAsync）。
        //
        // 因此下方按「由内向外（Innermost → Outermost）」顺序调用 builder.Use()，
        // 使运行时真实调用链从外到内严格为：
        //   1. ToolCallEvent（最外层：对所有调用发射 ToolStart / ToolDone，含被拦截调用）
        //   2. ToolResultUnwrap [外层兜底]（入口重置 ToolExecutionContext；出口将外层短路返回的 ToolResult 序列化为 string）
        //   3. ToolArgumentBinding（在任何安全/权限/契约检查前归一化 snake_case、别名与标量/数组参数）
        //   4. SafetyInvariant（Layer 0 安全不变量：命令/路径/URL 硬拦截）
        //   5. Hook（pre_tool_call / post_tool_call 拦截与审计）
        //   6. PermissionAndLimit（Layer 1：封条、白名单、权限检查与调用配额上限）
        //   7. StateMachine（Layer 2：指向性故障限流与编译验证恢复闸）
        //   8. EditGuard（Edit 目标存在性前置检查 + 编辑后编译验证写 IsVerificationFailure）
        //   9. EditTransaction（编辑前文件快照 + 编辑后增量 Diff 通知）
        //  10. ToolExecutionBudget（单次工具输出字符上限截断）
        //  11. ToolResultUnwrap [内层核心]（捕获编组/执行异常；将 AIFunction.InvokeAsync 返回的 JsonElement/ToolResult 解包并写 IsError）
        //  -> AIFunction.InvokeAsync
        var unwrapDelegate = new ToolResultUnwrapMiddleware(
            modelId: options.ModelId,
            providerId: options.ProviderId,
            logger: loggerFactory.CreateLogger<ToolResultUnwrapMiddleware>()).CreateDelegate();

        // 11. 最内层：紧贴 AIFunction.InvokeAsync 解包 JsonElement / ToolResult 并捕获执行异常
        builder = builder.Use(unwrapDelegate);

        // 10. 工具结果字符预算截断（依赖内层 Unwrap 输出的 string）
        builder = builder.Use(
            new ToolExecutionBudgetMiddleware(
                logger: loggerFactory.CreateLogger<ToolExecutionBudgetMiddleware>()).CreateDelegate());

        // 9. 编辑事务快照与增量 Diff（仅在前置安全/权限/契约全部通过后触发）
        if (options.EditTransaction is not null)
        {
            builder = builder.Use(
                new EditTransactionMiddleware(
                    options.EditTransaction,
                    options.WorkingDirectory,
                    options.FileChangeCallback,
                    loggerFactory.CreateLogger<EditTransactionMiddleware>()).CreateDelegate());
        }

        // 8. 编辑前置检查 + 编辑后编译/静态验证（验证失败时写 IsVerificationFailure 供外层状态机读取）
        var editVerification = options.EnableVerification ? options.VerificationProvider : null;
        if (options.EnableEditPreChecks || editVerification is not null)
        {
            builder = builder.Use(
                EditGuardMiddleware.Create(
                    options.EnableEditPreChecks,
                    editVerification,
                    options.WorkingDirectory,
                    Middleware.VerificationOptions.Default,
                    loggerFactory.CreateLogger("EditGuardMiddleware")));
        }

        // 7. 恢复状态机（包裹 EditGuard 与内层执行，读取真实执行错误与 IsVerificationFailure）
        if (options.EnableStateMachine)
        {
            builder = builder.Use(
                StateMachineMiddleware.Create(
                    loggerFactory.CreateLogger("StateMachineMiddleware")));
        }

        // 6. 权限与配额限制（位于状态机外层：被权限或配额拦截的调用不进入状态机）
        builder = builder.Use(PermissionAndLimitMiddleware.Create(
            options, metrics, loggerFactory.CreateLogger("PermissionAndLimitMiddleware")));

        // 5. Hook 拦截点（pre_tool_call / post_tool_call）
        if (options.HookExecutionService is not null)
        {
            builder = builder.Use(
                HookMiddleware.Create(options, loggerFactory.CreateLogger("HookMiddleware")));
        }

        // 4. Layer 0 安全不变量（无论何种角色与权限模式均强制执行）
        builder = builder.Use(
            SafetyInvariantMiddleware.Create(
                SafetyInvariantMiddleware.CreateDefaultSafetyInvariants(options.WorkingDirectory),
                loggerFactory.CreateLogger("SafetyInvariantMiddleware")));

        // 3. 参数名/别名/数组形状归一化（位于安全不变量与权限检查外层，确保检查与执行看到同一份规范参数）
        builder = builder.Use(
            new ToolArgumentBindingMiddleware(
                loggerFactory.CreateLogger<ToolArgumentBindingMiddleware>()).CreateDelegate());

        // 2. 外层兜底解包（将 SafetyInvariant / Hook / PermissionAndLimit / EditGuard 短路返回的 ToolResult 序列化为 string 并同步 IsError）
        builder = builder.Use(unwrapDelegate);

        // 1. 最外层：工具调用生命周期事件（确保被前置拦截的调用同样成对发射 ToolStart / ToolDone）
        if (options.OrchestrationEventSink is not null)
        {
            builder = builder.Use(
                ToolCallEventMiddleware.Create(
                    options, loggerFactory.CreateLogger("ToolCallEventMiddleware")));
        }

        return new AgentPipelineHandle(builder.Build(serviceProvider), metrics);
    }
}

