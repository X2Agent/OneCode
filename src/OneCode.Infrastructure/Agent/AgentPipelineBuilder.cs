using OneCode.Infrastructure.Middleware;
using OneCode.Infrastructure.Middleware.Contracts;
using OneCode.Infrastructure.Middleware.Invariants;
using OneCode.Infrastructure.Agent.RunMiddleware;
using OneCode.Core.Cost;
using OneCode.Core.Coordinator;
using OneCode.Core.Domain;
using OneCode.Core.Permissions;
using Microsoft.Agents.AI;
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

public sealed record AgentPipelineHandle(AIAgent Agent, AgentPipelineMetrics Metrics);

public sealed record ChatClientAgentBuildOptions
{
    public required IChatClient ChatClient { get; init; }
    public required string Name { get; init; }
    public required ChatOptions ChatOptions { get; init; }
    public required ILoggerFactory LoggerFactory { get; init; }
    public required IServiceProvider ServiceProvider { get; init; }
    public required AgentPipelineOptions PipelineOptions { get; init; }
    public IReadOnlyList<AIContextProvider>? ChatClientContextProviders { get; init; }
    public IReadOnlyList<AIContextProvider>? AgentContextProviders { get; init; }
    public ToolMetadataRegistry? ToolMetadata { get; init; }
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
    public IEnumerable<Func<ToolAutoApprovalRuleContext, ValueTask<bool>>>? AutoApprovalRules { get; init; }

    public IApprovalBroker? ApprovalBroker { get; init; }

    /// <summary>
    /// Team 路径 inline 审批委托。当 EnableToolApproval:false 且 PermissionDecision 为 Ask/Passthrough 时，
    /// 由权限中间件调用此委托进行 inline 审批。Main 路径不使用此字段（走 MAF ToolApprovalAgent 事件流）。
    /// </summary>
    public Func<string, System.Text.Json.JsonElement, CancellationToken, Task<bool>>? ApprovalHandler { get; init; }

    // Permission context fields
    // These populate ToolPermissionContext so that PermissionChecker strategies
    // can evaluate rules and validate paths against additional directories.

    /// <summary>User-configured permission rules (allow/deny/ask) keyed by source.</summary>
    public IReadOnlyDictionary<string, PermissionRuleGroup>? RulesBySource { get; init; }

    /// <summary>Additional working directories beyond the main WorkingDirectory.</summary>
    public IReadOnlyDictionary<string, AdditionalWorkingDirectory>? AdditionalWorkingDirectories { get; init; }

    /// <summary>Session-level allowlist of tool names that bypass permission checks.</summary>
    public HashSet<string>? SessionAllowlist { get; init; }


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
    /// ICostTracker 实例（可选）。当设置时，UsageTrackingRunMiddleware 会在
    /// Agent Run 级统一拦截 LLM 返回的 Usage 并写入 ICostTracker，确保所有路径
    /// （流式/非流式/Goal/Team/headless）的 token 成本都被记录，使
    /// <c>--max-budget-usd</c> 预算熔断在所有路径下生效。
    /// </summary>
    public ICostTracker? CostTracker { get; init; }

    /// <summary>
    /// 当前会话 ID。传递给 UsageTrackingRunMiddleware，使 ICostTracker 的 per-session
    /// 成本能正确记录，TokenUsageTracker 能从 ICostTracker 读取 session 级 token 计数。
    /// </summary>
    public SessionId? ConversationId { get; init; }

    /// <summary>
    /// 预算上限（USD，可选）。当设置且 <see cref="ICostTracker"/> 非空时，
    /// BudgetGuardRunMiddleware 会在 Agent Run 级执行 <b>pre-execution</b> 预算检查：
    /// 若 <see cref="ICostTracker.GetTotalCost"/> 已达到或超过此值，短路返回错误响应，
    /// 不发起 LLM 调用。null 表示不限制预算（不执行 pre-execution 检查）。
    /// post-execution 的预算状态报告仍由 MainAgentRunner 负责。
    /// </summary>
    public decimal? MaxBudgetUsd { get; init; }

}

public static class AgentPipelineBuilder
{
    public static AgentPipelineHandle BuildChatClientAgent(ChatClientAgentBuildOptions options)
    {
        var chatClient = options.ChatClient;
        if (options.ChatClientContextProviders is { Count: > 0 })
        {
            var builder = chatClient.AsBuilder();
            foreach (var provider in options.ChatClientContextProviders)
                builder = builder.UseAIContextProviders(provider);

            chatClient = builder.Build();
        }

        if (options.ChatOptions.Tools is { Count: > 0 } tools)
            options.ChatOptions.Tools = WrapApprovalRequiredTools(tools, options.ToolMetadata);

        var agentOptions = new ChatClientAgentOptions
        {
            Name = options.Name,
            ChatOptions = options.ChatOptions,
            AIContextProviders = options.AgentContextProviders,
            // Persist chat history after each service call for crash recovery.
            RequirePerServiceCallChatHistoryPersistence = true,
            // Enable mid-stream message injection (e.g., user interrupts).
            EnableMessageInjection = true,
            // In-memory chat history provider for within-loop history load/store.
            ChatHistoryProvider = new InMemoryChatHistoryProvider(),
        };

        var agent = new ChatClientAgent(
            chatClient,
            agentOptions,
            options.LoggerFactory,
            options.ServiceProvider);

        return Build(agent, options.PipelineOptions, options.LoggerFactory, options.ServiceProvider);
    }

    public static AgentPipelineHandle Build(
        ChatClientAgent agent,
        AgentPipelineOptions options,
        ILoggerFactory loggerFactory,
        IServiceProvider serviceProvider)
    {
        var metrics = new AgentPipelineMetrics();

        var builder = agent.AsBuilder();

        // Agent Run 级中间件（最外层）：BudgetGuard 预算守卫
        // pre-execution 检查：若 ICostTracker 累计成本已达 MaxBudgetUsd，短路返回错误响应，
        // 不发起 LLM 调用，防止超支后继续消费。位于 UsageTracking 外层，确保在任何
        // LLM 调用前拦截；短路时不产生 Usage，UsageTracking 内层不会被调用。
        if (options.CostTracker is not null && options.MaxBudgetUsd is not null)
        {
            var (guardRun, guardStream) = BudgetGuardRunMiddleware.Create(
                options.CostTracker,
                options.MaxBudgetUsd,
                loggerFactory.CreateLogger("BudgetGuardRunMiddleware"),
                options.ModelId);
            builder = builder.Use(guardRun, guardStream);
        }

        // Agent Run 级中间件（次外层）：Usage 追踪
        // 统一拦截所有 agent run（流式/非流式/Goal/Team/headless）的 LLM Usage，
        // 写入 ICostTracker，确保 --max-budget-usd 预算熔断在所有路径下生效。
        // 放在 BudgetGuard 内层：BudgetGuard 放行后，本层记录本次 run 的实际 usage；
        // 下一次 run 时 BudgetGuard 读取更新后的累计成本进行检查。
        if (options.CostTracker is not null)
        {
            var (runFunc, runStreamingFunc) = UsageTrackingRunMiddleware.Create(
                options.CostTracker,
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

        // Layer 0: Safety Invariants (BypassPermissions 也必须执行)
        if (options.EnableSafetyInvariants)
        {
            var invariants = options.SafetyInvariants
                ?? CreateDefaultSafetyInvariants(options.WorkingDirectory);
            builder = builder.Use(SafetyInvariantMiddleware.Create(
                invariants, loggerFactory.CreateLogger("SafetyInvariantMiddleware")));
        }

        // Hook（单段包裹 Pre/Post）
        // 传入 logger 用于 Pre/Post-hook 异常日志记录（Pre-hook 异常 fail-closed，
        // Post-hook 异常 fail-soft 保留工具结果）。
        // 仅在 HookExecutionService 可用时注册，避免无 hook 场景下的空调用层。
        if (options.HookExecutionService is not null)
        {
            builder = builder
                .Use(HookMiddleware.Create(options, loggerFactory.CreateLogger("HookMiddleware")));
        }

        // Tool call event streaming (TEAM mode transparency):
        // emits ToolStart/ToolDone events to the sink for real-time TUI display.
        // 传入 logger 用于 sink 异常日志记录，避免订阅者异常掩盖原始工具异常。
        if (options.OrchestrationEventSink is not null)
        {
            builder = builder.Use(ToolCallEventMiddleware.Create(
                options, loggerFactory.CreateLogger("ToolCallEventMiddleware")));
        }

        // Layer 1: 权限 + 工具上限
        builder = builder.Use(PermissionAndLimitMiddleware.Create(options, metrics));

        // Layer 2: 状态机 + 错误恢复（3-strike guidance 在 Main 路径开启，Worker/Team 关闭）
        if (options.EnableStateMachine)
        {
            builder = builder.Use(StateMachineMiddleware.Create(
                loggerFactory.CreateLogger("StateMachineMiddleware"),
                enableStrikeGuidance: options.EnableTaskRecovery));
        }

        if (options.EnableEditTransaction && options.EditTransaction is not null)
        {
            builder = builder.Use(new EditTransactionMiddleware(
                options.EditTransaction, options.WorkingDirectory, options.FileChangeCallback,
                loggerFactory.CreateLogger<EditTransactionMiddleware>()).CreateDelegate());
        }

        // Post-tool 验证检查（Layer 1）：在 EditTransaction 之后触发 build/type-check，
        // 验证失败时把错误回注 LLM 上下文。防抖：每 N 次源码文件编辑触发一次。
        // 多语言路由：由 IVerificationProvider.IsSourceFile 判断 + VerificationProfile 配置驱动。
        if (options.EnableVerification && options.VerificationProvider is not null)
        {
            builder = builder.Use(new VerificationMiddleware(
                options.VerificationProvider,
                options.WorkingDirectory,
                options.VerificationOptions ?? Middleware.VerificationOptions.Default,
                loggerFactory.CreateLogger<VerificationMiddleware>()).CreateDelegate());
        }

        if (options.EnableToolResultBudget)
        {
            builder = builder.Use(new ToolExecutionBudgetMiddleware(
                logger: loggerFactory.CreateLogger<ToolExecutionBudgetMiddleware>()).CreateDelegate());
        }

        // ToolResult 解包（双职责）：
        //   1. ToolResult → string：经 ToolResultSerializer 按模型能力序列化（JSON/Markdown），
        //      避免 IChatClient 适配器把 record 序列化为 JSON 包装对象；下游 ToolExecutionBudget
        //      （按长度截断）依赖 string 输入。
        //   2. ToolResult.IsError → ToolExecutionContext.IsError：用强类型字段传递错误语义，
        //      外层 StateMachine/ToolCallEvent 从 context 读取，不依赖字符串前缀匹配。
        //   3. 可恢复错误检测（overloaded/529）：检查 ToolResult 内容，标记为 Recovery guidance。
        // 位置：在 Contract（返回 ToolResult）之外，
        //      在 ToolExecutionBudget（消费 string）之内。
        builder = builder.Use(new ToolResultUnwrapMiddleware(
            modelId: options.ModelId,
            providerId: options.ProviderId,
            logger: loggerFactory.CreateLogger<ToolResultUnwrapMiddleware>()).CreateDelegate());

        // 行为契约（Layer 2）：工具执行前后验证
        if (options.EnableBehaviorContracts && options.BehaviorContracts is not null)
        {
            builder = builder.Use(ContractMiddleware.Create(
                options.BehaviorContracts,
                loggerFactory.CreateLogger("ContractMiddleware")));
        }

        // MAF UseToolApproval: satisfies MAF protocol for built-in ToolApprovalRequestContent.
        //
        // Architecture (single-gate model):
        //   Permission middleware (CheckPermissionAndExecuteAsync) handles Allow/Deny.
        //   Ask/Passthrough → 放行到此层，由 AutoApprovalRules 决定：
        //     匹配规则 → 自动放行
        //     不匹配 → 产生 ToolApprovalRequestContent → MainAgentRunner 事件驱动审批 → 续跑
        //
        // Main 路径通过 options.AutoApprovalRules 传入（含 AgentSkillsProvider 规则）；
        // Worker/Team 通过 AutoApprovalRulesFactory 从 Profile 兜底生成。
        if (options.EnableToolApproval)
        {
            var rules = options.AutoApprovalRules
                ?? AutoApprovalRulesFactory.Create(options.PermissionMode);

            builder = builder.UseToolApproval(new ToolApprovalAgentOptions
            {
                AutoApprovalRules = rules,
            });
        }

        return new AgentPipelineHandle(builder.Build(serviceProvider), metrics);
    }

    private static IList<AITool> WrapApprovalRequiredTools(
        IEnumerable<AITool> tools,
        ToolMetadataRegistry? metadata)
    {
        return tools
            .Select(tool =>
            {
                if (tool is not AIFunction function)
                    return tool;

                var requiresBoundary = metadata is null
                    || metadata.RequiresApprovalBoundary(function.Name);

                return requiresBoundary && function is not ApprovalRequiredAIFunction
                    ? new ApprovalRequiredAIFunction(function)
                    : tool;
            })
            .ToList();
    }

    /// <summary>创建默认安全不变量列表。</summary>
    private static IReadOnlyList<ISafetyInvariant> CreateDefaultSafetyInvariants(string workingDirectory) =>
    [
        new FileSystemInvariant(workingDirectory),
        new BashCommandInvariant(),
        new ResourceInvariant(),
    ];
}
