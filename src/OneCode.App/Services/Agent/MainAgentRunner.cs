using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OneCode.App.Services.Compact;
using OneCode.Core.Build;
using OneCode.Infrastructure.Agent;

namespace OneCode.App.Services.Agent;

/// <summary>
/// MAF-based main agent runner（主查询循环）。
/// 约束：不能在根 IChatClient 单例级别应用 AIContextProviders（见 ServiceCollectionExtensions.cs）。
/// </summary>
public partial class MainAgentRunner : IMainAgentRunner
{
    // IServiceProvider 仅用于传递给 MAF 的 ChatClientAgentBuildOptions.ServiceProvider
    // （MAF 框架要求），不得用于业务逻辑中的 GetService<T>() 调用。
    private readonly IServiceProvider _serviceProvider;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<MainAgentRunner> _logger;
    private readonly IChatClient _chatClient;
    private readonly Core.Tools.ToolMetadataRegistry _toolMetadata;
    private readonly MainModeContextProviderBuilder _mainContextBuilder;
    private readonly AgentPipelineAssembly _pipelineAssembly;
    private readonly CompactionProviderBuilder _compactionBuilder;
    private readonly AgentSessionStore _sessionStore;
    private readonly IToolProtocolValidator _toolProtocolValidator;
    private readonly IVerificationProvider? _verificationProvider;

    public MainAgentRunner(
        MainModeContextProviderBuilder mainContextBuilder,
        AgentPipelineAssembly pipelineAssembly,
        CompactionProviderBuilder compactionBuilder,
        AgentSessionStore sessionStore,
        IChatClient chatClient,
        ILoggerFactory loggerFactory,
        IServiceProvider serviceProvider,
        Core.Tools.ToolMetadataRegistry toolMetadata,
        IToolProtocolValidator? toolProtocolValidator = null,
        IVerificationProvider? verificationProvider = null)
    {
        _mainContextBuilder = mainContextBuilder;
        _pipelineAssembly = pipelineAssembly;
        _compactionBuilder = compactionBuilder;
        _sessionStore = sessionStore;
        _chatClient = chatClient;
        _loggerFactory = loggerFactory;
        _serviceProvider = serviceProvider;
        _logger = loggerFactory.CreateLogger<MainAgentRunner>();
        _toolMetadata = toolMetadata;
        _toolProtocolValidator = toolProtocolValidator ?? new ToolProtocolValidator();
        _verificationProvider = verificationProvider;
    }

    /// <summary>
    /// 构建已装配全部中间件的 <see cref="AIAgent"/>，供外部编排器（如 Goal 模式的 LoopAgent）包装使用。
    ///
    /// 返回的 AIAgent 已包含：
    /// - 权限检查中间件（PermissionChecker）
    /// - Hook 中间件（HookExecutionService）
    /// - 工具审批（ToolApprovalAgent）
    /// - 事务管理（EditTransaction）
    /// - OrchestrationEventSink（工具调用事件推送到 TUI）
    /// - 全量 AIContextProvider（Skills/Memory/Design/LSP/Todo/Shell/Compaction）
    ///
    /// 注意：此方法不包含 RunAsync/RunStreamingAsync 中的 PromptTooLong 恢复循环，
    /// 因为 CompactionProvider 已在 context provider 层面处理压缩。
    /// 调用方负责创建/管理 AgentSession 并调用 RunAsync/RunStreamingAsync。
    /// </summary>
    /// <param name="options">
    /// 运行选项。<see cref="MainAgentRunOptions.SharedTransaction"/> 必须提供——
    /// 调用方负责事务生命周期（Commit/Dispose）。
    /// </param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>已装配中间件的 AIAgent。</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="options"/>.<see cref="MainAgentRunOptions.SharedTransaction"/> 为 null。
    /// </exception>
    public virtual async Task<AIAgent> BuildAsAIAgentAsync(
        MainAgentRunOptions options,
        CancellationToken ct = default)
    {
        if (options.SharedTransaction is null)
            throw new ArgumentNullException(
                nameof(options),
                "SharedTransaction is required when using BuildAsAIAgentAsync. " +
                "The caller owns the transaction lifecycle (Commit/Dispose).");

        var cwd = options.WorkingDirectory ?? Environment.CurrentDirectory;
        var transaction = options.SharedTransaction;

        var pipeline = await BuildAgentPipelineAsync(options, transaction, cwd, ct).ConfigureAwait(false);
        return pipeline.Agent;
    }

    /// <summary>
    /// Streams through MAF ChatClientAgent.RunStreamingAsync and forwards events to the caller.
    /// Each turn is written to the channel as it arrives.
    /// Returns a <see cref="MainAgentRunResult"/> with the real terminal reason and transaction state.
    /// </summary>
    public virtual async Task<MainAgentRunResult> RunStreamingAsync(
        MainAgentRunOptions options,
        System.Threading.Channels.ChannelWriter<object> writer,
        CancellationToken ct = default)
    {
        _logger.LogDebug("MainAgentRunner streaming (model={ModelId}, maxTurns={MaxTurns})",
            options.ModelId, options.MaxTurns);

        // OBS-1.1: 统一 conversationId 到 Activity baggage，与 ERR-1 traceId 协同日志关联
        System.Diagnostics.Activity.Current?.SetBaggage(
            "conversationId", options.ConversationId?.ToString() ?? string.Empty);

        var cwd = options.WorkingDirectory ?? Environment.CurrentDirectory;
        // 共享事务：由调用方管理生命周期（GOAL 模式多子目标共用）。
        // 独立事务：本方法内部创建，成功时 commit，异常时 dispose 回滚。
        var ownsTransaction = options.SharedTransaction is null;
        var transaction = options.SharedTransaction ?? new EditTransaction(
            _loggerFactory.CreateLogger<EditTransaction>());

        BuildTerminalReason terminalReason = BuildTerminalReason.Completed;
        bool transactionCommitted = false;
        bool transactionRolledBack = false;
        BuildValidationStatus finalValidationStatus = BuildValidationStatus.Passed;
        IReadOnlyList<string> modifiedFiles = [];
        string? validationFailureSummary = null;
        var evidence = new MainAgentRunEvidenceCollector(
            options.AgentRunId ?? Guid.NewGuid().ToString("N"));

        try
        {
            var chatMessages = BuildMessages(options);
            var validation = _toolProtocolValidator.Validate(chatMessages);
            if (!validation.IsValid)
            {
                _logger.LogError(
                    "Provider request blocked by invalid tool protocol for conversation {ConversationId}: {Errors}",
                    options.ConversationId,
                    string.Join("; ", validation.Errors.Select(error =>
                        $"{error.Code}[{error.CallId}]@{error.MessageIndex}")));
                throw new ToolProtocolException(validation);
            }

            var runOptions = options with
            {
                ApprovalBroker = options.SuppressToolApproval
                    ? null
                    : ApprovalBroker.ForQuery(
                        writer,
                        _loggerFactory.CreateLogger<ApprovalBroker>()),
            };

            // PromptTooLong 恢复由 PromptTooLongRecoveryRunMiddleware 在 Agent Run 级处理，
            // Runner 层不再包裹恢复循环 — 符合 MAF 最佳实践（异常在 middleware 层拦截）。
            var pipeline = await BuildAgentPipelineAsync(runOptions, transaction, cwd, ct: ct).ConfigureAwait(false);
            var builtAgent = pipeline.Agent;

            _logger.LogDebug("MainAgentRunner session creating...");
            var session = await _sessionStore.CreateOrRestoreSessionAsync(
                builtAgent,
                runOptions.ConversationId,
                ct).ConfigureAwait(false);
            _logger.LogDebug("MainAgentRunner session created, starting agent stream...");

            // PERM-1.6~1.8: 流式审批循环
            // 收集 updates 时检测 ToolApprovalRequestContent，推送 ApprovalRequestEvent 到 channel，
            // TUI 消费后通过 ResponseSource 回传决策，构造 response 续跑。
            var currentMessages = chatMessages;
            const int maxApprovalRounds = 50;
            int approvalRound = 0;

            while (true)
            {
                var approvalRequests = new List<ToolApprovalRequestContent>();

                await foreach (var evt in builtAgent.RunStreamingAsync(
                    currentMessages, session, new AgentRunOptions(), ct).ConfigureAwait(false))
                {
                    // PERM-1.6: 检测 ToolApprovalRequestContent，不输出给用户
                    var approvalReq = evt.Contents?.OfType<ToolApprovalRequestContent>().FirstOrDefault();
                    if (approvalReq is not null)
                    {
                        approvalRequests.Add(approvalReq);
                    }
                    else
                    {
                        evidence.Observe(evt);
                        await writer.WriteAsync(evt, ct).ConfigureAwait(false);
                    }
                }

                // 无审批请求或达到上限 → 结束循环
                if (approvalRequests.Count == 0)
                    break;

                // 达到审批轮数上限：丢弃本轮审批请求前记录告警，避免静默丢失用户决策
                if (++approvalRound > maxApprovalRounds)
                {
                    _logger.LogWarning(
                        "Approval round limit reached ({MaxRounds}); discarding {PendingCount} pending approval request(s)",
                        maxApprovalRounds, approvalRequests.Count);
                    break;
                }

                // PERM-1.7: 推送 ApprovalRequestEvent 并构造续跑 input
                var approvalMessages = new List<ChatMessage>();
                foreach (var req in approvalRequests)
                {
                    var approvalResponse = await HandleToolApprovalAsync(
                        req,
                        runOptions.ApprovalBroker!,
                        ct).ConfigureAwait(false);
                    approvalMessages.Add(new ChatMessage(ChatRole.User, [approvalResponse]));
                }
                currentMessages = approvalMessages;
            }

            await _sessionStore.PersistSessionAsync(
                builtAgent,
                session,
                runOptions.ConversationId,
                ct).ConfigureAwait(false);

            // Final validation before commit: Build mode may provide a shared transaction
            // whose commit is deferred until the BuildRun final decision is durably persisted.
            var validatesBeforeExternalCommit = ownsTransaction || options.DeferTransactionCommit;
            if (validatesBeforeExternalCommit && options.BeforeFinalValidation is not null)
                await options.BeforeFinalValidation(ct).ConfigureAwait(false);

            if (validatesBeforeExternalCommit && transaction.SnapshotCount > 0)
            {
                modifiedFiles = transaction.GetModifiedFiles();
                if (_verificationProvider is null)
                {
                    finalValidationStatus = BuildValidationStatus.Skipped;
                    validationFailureSummary = "Final validation is unavailable because no verification provider is registered.";
                    terminalReason = BuildTerminalReason.ValidationFailed;
                    transactionRolledBack = true;
                    return BuildResult();
                }

                var finalCheck = await _verificationProvider.VerifyAsync(
                    cwd, modifiedFiles, ct).ConfigureAwait(false);
                finalValidationStatus = finalCheck.Skipped
                    ? BuildValidationStatus.Skipped
                    : finalCheck.Success
                        ? BuildValidationStatus.Passed
                        : BuildValidationStatus.Failed;

                if (finalValidationStatus != BuildValidationStatus.Passed)
                {
                    _logger.LogWarning(
                        "Final validation did not pass (status={Status}, errors={ErrorCount}) — rolling back {FileCount} file changes",
                        finalValidationStatus, finalCheck.Errors.Count, transaction.SnapshotCount);

                    validationFailureSummary = finalCheck.FormatForLlm();
                    terminalReason = BuildTerminalReason.ValidationFailed;
                    transactionRolledBack = true;
                    return BuildResult();
                }

                _logger.LogDebug("Final validation passed after agent run");
            }

            // Shared transactions and explicitly deferred Build transactions are committed
            // by their coordinator after the durable business decision is saved.
            if (ownsTransaction && !options.DeferTransactionCommit)
            {
                transaction.Commit();
                transactionCommitted = true;
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("MainAgentRunner streaming cancelled");
            terminalReason = BuildTerminalReason.Cancelled;
            transactionRolledBack = ownsTransaction && !transaction.IsCommitted;
        }
        finally
        {
            // 仅独立事务时 dispose（触发回滚若未 commit）；共享事务由创建者管理。
            if (ownsTransaction)
                ((IDisposable)transaction).Dispose();
            writer.TryComplete();
        }

        return BuildResult();

        MainAgentRunResult BuildResult() => new(
            Text: null,
            TotalInputTokens: evidence.InputTokens,
            TotalOutputTokens: evidence.OutputTokens,
            TurnCount: evidence.TurnCount,
            BudgetExceeded: evidence.BudgetExceeded,
            BudgetExceededReason: evidence.BudgetExceeded ? "Agent budget was exceeded." : null,
            TerminalReason: evidence.BudgetExceeded ? BuildTerminalReason.BudgetExceeded : terminalReason,
            TransactionCommitted: transactionCommitted,
            TransactionRolledBack: transactionRolledBack,
            FinalValidationStatus: finalValidationStatus,
            ModifiedFiles: modifiedFiles,
            ValidationFailureSummary: validationFailureSummary,
            CompletedToolBatches: evidence.CompletedToolBatches);
    }

    private static ChatOptions BuildChatOptions(MainAgentRunOptions options)
    {
        var chatOptions = new ChatOptions
        {
            ModelId = options.ModelId,
            MaxOutputTokens = options.MaxOutputTokens ?? 4096,
        };

        // Thinking settings are translated by ProviderAwareDecorator.
        if (options.EnableThinking)
        {
            chatOptions.AdditionalProperties ??= new AdditionalPropertiesDictionary();
            if (options.ThinkingBudgetTokens > 0)
                chatOptions.AdditionalProperties["thinking_budget"] = options.ThinkingBudgetTokens;
            if (!string.IsNullOrEmpty(options.ThinkingEffort))
                chatOptions.AdditionalProperties["thinking_effort"] = options.ThinkingEffort;
        }

        // Tool approval wrapping is centralized in AgentPipelineBuilder from
        // ToolMetadataRegistry. Keeping it there prevents Main/Worker/Team policy drift.
        if (options.Tools is { Count: > 0 })
        {
            chatOptions.Tools = options.Tools.ToList();
            chatOptions.ToolMode = ChatToolMode.Auto;
        }

        return chatOptions;
    }

    private static List<ChatMessage> BuildMessages(MainAgentRunOptions options)
    {
        List<ChatMessage> messages = [];

        if (!string.IsNullOrEmpty(options.SystemPrompt))
            messages.Add(new ChatMessage(ChatRole.System, options.SystemPrompt));

        if (options.Messages is { Count: > 0 })
            messages.AddRange(options.Messages);

        // Prefer pre-built UserMessage (may contain multimodal content) over plain text.
        if (options.UserMessage is not null)
            messages.Add(options.UserMessage);
        else if (!string.IsNullOrEmpty(options.UserPrompt))
            messages.Add(new ChatMessage(ChatRole.User, options.UserPrompt));

        return messages;
    }

    /// <summary>
    /// 从 <see cref="UsageDetails.AdditionalCounts"/> 中提取缓存写 token
    /// （Anthropic 的 cache_creation_input_tokens）。
    /// 与 UsageTrackingRunMiddleware.BuildUsageRecord 和 ChatService.ExtractAdditionalCount 逻辑一致。
    /// </summary>
    private static long ExtractCacheWriteTokens(UsageDetails? usage)
    {
        if (usage?.AdditionalCounts is null) return 0;

        foreach (var key in s_cacheWriteKeys)
        {
            if (usage.AdditionalCounts.TryGetValue(key, out var value))
                return value;
        }

        return 0;
    }

    private static readonly string[] s_cacheWriteKeys =
    [
        "cache_creation_input_tokens",
        "cache_creation",
        "cacheWriteInputTokens",
        "cache_write_input_tokens",
    ];
}
