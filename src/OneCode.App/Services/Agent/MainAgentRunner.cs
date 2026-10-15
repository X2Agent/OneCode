using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OneCode.App.Services.Compact;
using OneCode.Core.Build;
using OneCode.Infrastructure.Agent;
using OneCode.Infrastructure.Ai;

namespace OneCode.App.Services.Agent;

/// <summary>
/// MAF-based main agent runner（主查询循环）。
/// 约束：不能在根 IChatClient 单例级别应用 AIContextProviders（见 ServiceCollectionExtensions.cs）。
/// </summary>
public partial class MainAgentRunner(
    AgentContextPipeline contextPipeline,
    AgentPipelineAssembly pipelineAssembly,
    CompactionStrategyFactory compactionBuilder,
    AgentSessionPersistence sessionStore,
    IChatClient chatClient,
    ILoggerFactory loggerFactory,
    IServiceProvider serviceProvider,
    Core.Tools.ToolMetadataRegistry toolMetadata,
    IToolProtocolValidator? toolProtocolValidator = null,
    IVerificationProvider? verificationProvider = null,
    TodoProjectionService? todoProjection = null) : IMainAgentRunner
{
    // IServiceProvider 仅用于传递给 MAF 的 ChatClientAgentBuildOptions.ServiceProvider
    // （MAF 框架要求），不得用于业务逻辑中的 GetService<T>() 调用。
    private readonly IServiceProvider _serviceProvider = serviceProvider;
    private readonly ILoggerFactory _loggerFactory = loggerFactory;
    private readonly ILogger<MainAgentRunner> _logger = loggerFactory.CreateLogger<MainAgentRunner>();
    private readonly IChatClient _chatClient = chatClient;
    private readonly Core.Tools.ToolMetadataRegistry _toolMetadata = toolMetadata;
    private readonly AgentContextPipeline _contextPipeline = contextPipeline;
    private readonly AgentPipelineAssembly _pipelineAssembly = pipelineAssembly;
    private readonly CompactionStrategyFactory _compactionBuilder = compactionBuilder;
    private readonly AgentSessionPersistence _sessionStore = sessionStore;
    private readonly IToolProtocolValidator _toolProtocolValidator = toolProtocolValidator ?? new ToolProtocolValidator();
    private readonly IVerificationProvider? _verificationProvider = verificationProvider;
    private readonly TodoProjectionService? _todoProjection = todoProjection;

    // 输出预算耗尽（Length + 零文本零工具调用）时的升级值与默认输出上限共用
    // MaxOutputTokensDecorator.EscalatedMaxTokens 单一事实源。
    internal const int EscalatedZeroOutputRetryMaxTokens = MaxOutputTokensDecorator.EscalatedMaxTokens;

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
    /// 注意：此方法不包含 PromptTooLong 恢复逻辑——run 输入仅含本轮用户消息
    /// （历史由 TranscriptChatHistoryProvider 在会话层持有），截断重试无效；
    /// 上下文超限由 CompactionProvider 在 context provider 层面压缩处理，
    /// run 失败时由 QueryStreamEngine 给出用户提示。
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

        var pipeline = (await BuildAgentPipelineAsync(options, transaction, cwd, ct: ct).ConfigureAwait(false)).Handle;
        return pipeline.Agent;
    }

    /// <summary>
    /// Streams through the MAF HarnessAgent and forwards events to the caller.
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

        RunTerminalReason terminalReason = RunTerminalReason.Completed;
        bool transactionCommitted = false;
        bool transactionRolledBack = false;
        BuildValidationStatus finalValidationStatus = BuildValidationStatus.Passed;
        IReadOnlyList<string> modifiedFiles = [];
        string? validationFailureSummary = null;
        var evidence = new MainAgentRunEvidenceCollector(
            options.AgentRunId ?? Guid.NewGuid().ToString("N"));

        // Declared outside the try so the finally block can release the per-run providers on every
        // exit path, including a failure before the stream starts.
        AgentPipelineHandle? pipeline = null;

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
                        options.OnPermissionPrompt,
                        _loggerFactory.CreateLogger<ApprovalBroker>()),
                // 预算预警：70%/90% 经事件流发横幅事件（TUI 渲染黄/橙预警）
                OnBudgetWarning = (level, used, limit)
                    => writer.TryWrite(new Query.BudgetWarningEvent(level, used, limit)),
                // 预算 100%：交互模式弹窗三选一（追加预算继续/收尾总结/直接停止）；
                // 无人值守（SuppressToolApproval，如受控 Build/GOAL 编排）保持 null → 硬停短路
                OnBudgetExceeded = options.SuppressToolApproval
                    ? null
                    : (used, limit, token) => RequestBudgetDecisionAsync(writer, used, limit, token),
            };

            // 上下文超限不在 pipeline 内恢复（run 输入只有本轮用户消息，截断重试无效）：
            // 检测后附加可操作提示（/compact），失败收场逻辑见 OnRunFailedAsync。
            var built = await BuildAgentPipelineAsync(runOptions, transaction, cwd, ct: ct).ConfigureAwait(false);
            pipeline = built.Handle;
            // 零输出重试若不传首轮基线，装配层会就地重快照 ledger，
            // 首轮 attempt 的真实消耗将逃出 BUILD 预算（见重试处）。
            var originalBudgetBaseline = built.BudgetBaselineTokens;
            var builtAgent = pipeline.Agent;

            _logger.LogDebug("MainAgentRunner session creating...");
            var session = await _sessionStore.CreateOrRestoreSessionAsync(
                builtAgent,
                runOptions.ConversationId,
                ct).ConfigureAwait(false);
            _logger.LogDebug("MainAgentRunner session created, starting agent stream...");

            if (options.SeedTodoTasks is { Count: > 0 } seedTasks)
                await PlanTodoSeeder.SeedAsync(builtAgent, session, seedTasks, ct).ConfigureAwait(false);

            // 待办投影（初始态）：恢复的会话可能带着上一轮留下的待办——先发一次快照，
            // 即使本轮随后被取消，TUI 也能看到清单。run 的最终快照见工具循环之后。
            if (_todoProjection is not null)
                await _todoProjection.PublishAsync(builtAgent, session, ct).ConfigureAwait(false);

            // MAF owns approval binding and function-call state. The runner only bridges surfaced
            // requests to the TUI and resumes the same session with the responses: ToolApprovalAgent
            // queues excess unapproved requests, surfaces them one at a time, and executes the tools
            // only when the host re-enters with responses.
            var currentMessages = chatMessages;
            var zeroOutputLengthRetried = false;
            var todoMutations = new TodoMutationObserver();

            while (true)
            {
                List<ToolApprovalRequestContent> approvalRequests = [];

                await foreach (var evt in builtAgent.RunStreamingAsync(
                    currentMessages, session, new AgentRunOptions(), ct).ConfigureAwait(false))
                {
                    // 待办投影（工具边界）：todos_* 变更完成的那一刻就刷新快照，而不是等整个 run 结束——
                    // 一个 run 可长达十几分钟，run 末才刷新会让步骤进度在界面上长时间停滞。
                    if (_todoProjection is not null && todoMutations.Observe(evt))
                        await _todoProjection.PublishAsync(builtAgent, session, ct).ConfigureAwait(false);

                    // An update can carry approval requests alongside ordinary content: the framework
                    // converts every call in a response once any tool in it requires approval, and on
                    // reaching its auto-approval cap it returns a multi-request batch. Splitting the
                    // contents rather than dropping the whole update keeps the text the model produced
                    // — losing it would silently truncate the visible answer.
                    var approvalContents = evt.Contents?
                        .OfType<ToolApprovalRequestContent>()
                        .ToList();

                    if (approvalContents is not { Count: > 0 })
                    {
                        evidence.Observe(evt);
                        await writer.WriteAsync(evt, ct).ConfigureAwait(false);
                        continue;
                    }

                    approvalRequests.AddRange(approvalContents);

                    var nonApprovalContents = evt.Contents!
                        .Where(content => content is not ToolApprovalRequestContent)
                        .ToList();

                    if (nonApprovalContents.Count == 0)
                        continue;

                    var visibleUpdate = new AgentResponseUpdate(evt.Role, nonApprovalContents)
                    {
                        ResponseId = evt.ResponseId,
                        MessageId = evt.MessageId,
                        AuthorName = evt.AuthorName,
                    };
                    evidence.Observe(visibleUpdate);
                    await writer.WriteAsync(visibleUpdate, ct).ConfigureAwait(false);
                }

                if (approvalRequests.Count != 0)
                {
                    // MAF binds these responses to the surfaced tool calls on the next request, and
                    // ToolApprovalAgent injects the whole batch as ONE user message
                    // (InjectCollectedResponses) — mirror that shape instead of one message per response.
                    var approvalResponses = new List<AIContent>(approvalRequests.Count);
                    foreach (var req in approvalRequests)
                    {
                        approvalResponses.Add(await HandleToolApprovalAsync(
                            req,
                            runOptions.ApprovalBroker,
                            ct).ConfigureAwait(false));
                    }
                    currentMessages = [new ChatMessage(ChatRole.User, approvalResponses)];
                    continue;
                }

                // 流式零输出兜底：推理模型可能把全部输出预算耗在 reasoning 上，返回
                // Length + 零文本零工具调用（BuildRun 随即以 Failed 收场，用户侧表现为
                // "没有任何内容产生"）。无工具调用的 run 没有副作用，整轮重试幂等安全；
                // 流式无法中途 continuation（MaxOutputTokensDecorator 的升级+续跑恢复只
                // 覆盖非流式路径），只能在 run 结束后以提升的输出上限重跑，限一次。
                if (ShouldRetryZeroOutputLength(
                        zeroOutputLengthRetried, evidence.TurnCount, evidence.ToolCallCount, evidence.FinishReason))
                {
                    zeroOutputLengthRetried = true;
                    _logger.LogWarning(
                        "Zero-output run hit the output token cap (out={OutputTokens}) — retrying once with MaxOutputTokens={Escalated}",
                        evidence.OutputTokens,
                        EscalatedZeroOutputRetryMaxTokens);

                    // 首次 attempt 的 pipeline 租约就地释放，让位给重建的 pipeline（finally 只释放最后一个）。
                    pipeline?.ContextProviderLease?.Dispose();
                    todoMutations = new TodoMutationObserver();
                    pipeline = (await BuildAgentPipelineAsync(
                        runOptions with { MaxOutputTokens = EscalatedZeroOutputRetryMaxTokens },
                        transaction,
                        cwd,
                        budgetBaselineTokens: originalBudgetBaseline,
                        ct: ct).ConfigureAwait(false)).Handle;
                    builtAgent = pipeline.Agent;
                    // 复用同一会话：首轮零输出最多只会让历史里多一条 user 消息，
                    // 重试的可见收益远大于可能的重复提示。
                    session = await _sessionStore.CreateOrRestoreSessionAsync(
                        builtAgent,
                        runOptions.ConversationId,
                        ct).ConfigureAwait(false);
                    if (options.SeedTodoTasks is { Count: > 0 } retrySeedTasks)
                        await PlanTodoSeeder.SeedAsync(builtAgent, session, retrySeedTasks, ct).ConfigureAwait(false);
                    continue;
                }

                break;
            }

            // 产出画像诊断日志：2026-09 的"对话静默停止"事故中，模型返回 200 + 零内容
            // 流式响应后运行按 Completed 收尾，全链路无任何日志，只能靠时间差推断。
            if (evidence.TurnCount == 0 && evidence.ToolCallCount == 0)
            {
                _logger.LogWarning(
                    "Agent run completed with zero output (no text, no tool calls, finishReason={FinishReason}, in={InputTokens}, out={OutputTokens}) — the model likely returned an empty response",
                    evidence.FinishReason?.ToString() ?? "none",
                    evidence.InputTokens,
                    evidence.OutputTokens);
            }
            else
            {
                _logger.LogDebug(
                    "Agent run finished (terminal={TerminalReason}, turns={Turns}, toolCalls={ToolCalls}, in={InputTokens}, out={OutputTokens}, finishReason={FinishReason})",
                    terminalReason,
                    evidence.TurnCount,
                    evidence.ToolCallCount,
                    evidence.InputTokens,
                    evidence.OutputTokens,
                    evidence.FinishReason?.ToString() ?? "none");
            }

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
                    // 未注册验证器 = 无验证可跑。这是"验证不适用"而非失败——
                    // 回滚会静默删除用户已成功写入的文件。
                    finalValidationStatus = BuildValidationStatus.Skipped;
                }
                else
                {
                    var finalCheck = await _verificationProvider.VerifyAsync(
                        cwd, modifiedFiles, ct).ConfigureAwait(false);
                    finalValidationStatus = finalCheck.Skipped
                        ? BuildValidationStatus.Skipped
                        : finalCheck.Success
                            ? BuildValidationStatus.Passed
                            : BuildValidationStatus.Failed;

                    // 仅真实验证失败才回滚。Skipped（修改的文件没有匹配的验证 profile，
                    // 如生成的 HTML 报告）表示验证不适用——按失败处理会把已写入的
                    // 文件静默删除，而对话里模型早已报告"文件已生成"。
                    if (ShouldRollBackForValidation(finalValidationStatus))
                    {
                        _logger.LogWarning(
                            "Final validation failed (status={Status}, errors={ErrorCount}) with {FileCount} changed files (rolledBack={RolledBack})",
                            finalValidationStatus, finalCheck.Errors.Count, transaction.SnapshotCount,
                            !options.DeferTransactionCommit);

                        validationFailureSummary = finalCheck.FormatForLlm();
                        terminalReason = RunTerminalReason.ValidationFailed;
                        // 受控 Build attempt（延迟提交）把文件处置权交给其 runtime：
                        // 验证失败保留产物供人工复核，不在此宣告回滚。
                        transactionRolledBack = !options.DeferTransactionCommit;
                        await PersistSessionAndPublishTodosAsync().ConfigureAwait(false);
                        return BuildResult();
                    }

                    _logger.LogDebug("Final validation finished (status={Status})", finalValidationStatus);
                }
            }

            // 计划预置待办的收尾补账：受控 Build 正常完成且验证通过（且非零交付物）时，
            // 将剩余未勾选的待办一并标为完成，避免模型漏调 todos_complete 让已完成计划残留 "1/7" 未完成态。
            if (options.SeedTodoTasks is { Count: > 0 } completedSeedTasks
                && terminalReason == RunTerminalReason.Completed
                && (transaction.SnapshotCount > 0 || completedSeedTasks.All(task => task.ExpectedFiles.Count == 0)))
            {
                await PlanTodoSeeder.CompleteRemainingAsync(builtAgent, session, ct).ConfigureAwait(false);
            }

            await PersistSessionAndPublishTodosAsync().ConfigureAwait(false);

            // Shared transactions and explicitly deferred Build transactions are committed
            // by their coordinator after the durable business decision is saved.
            if (ownsTransaction && !options.DeferTransactionCommit)
            {
                transaction.Commit();
                transactionCommitted = true;
            }

            async Task PersistSessionAndPublishTodosAsync()
            {
                await _sessionStore.PersistSessionAsync(
                    builtAgent,
                    session,
                    runOptions.ConversationId,
                    ct).ConfigureAwait(false);

                // 待办投影（最终态）：一次 run（含审批续跑的全部迭代与收尾补账）结束后发布权威快照，
                // 兜住流式循环里没有观察到变更工具结果的路径（如取消前最后一批）。
                if (_todoProjection is not null)
                    await _todoProjection.PublishAsync(builtAgent, session, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("MainAgentRunner streaming cancelled");
            terminalReason = RunTerminalReason.Cancelled;
            transactionRolledBack = ownsTransaction && !transaction.IsCommitted;
        }
        finally
        {
            // 仅独立事务时 dispose（触发回滚若未 commit）；共享事务由创建者管理。
            if (ownsTransaction)
                ((IDisposable)transaction).Dispose();

            // Release per-run context providers after the last use. Placed here rather than after the
            // stream loop so cancellation and exceptions release too, and a stream abandoned by the
            // consumer still runs this path when the runner unwinds.
            pipeline?.ContextProviderLease?.Dispose();

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
            TerminalReason: evidence.BudgetExceeded ? RunTerminalReason.BudgetExceeded : terminalReason,
            TransactionCommitted: transactionCommitted,
            TransactionRolledBack: transactionRolledBack,
            FinalValidationStatus: finalValidationStatus,
            ModifiedFiles: modifiedFiles,
            ValidationFailureSummary: validationFailureSummary,
            CompletedToolBatches: evidence.CompletedToolBatches,
            ToolCallCount: evidence.ToolCallCount);
    }

    /// <summary>
    /// 仅真实验证失败才回滚文件修改。Skipped（修改的文件没有匹配的验证 profile、
    /// 验证命令不可用或未注册验证器）表示"验证不适用"——旧实现按 != Passed 判定，
    /// 会把 Skipped 当失败并静默删除已成功写入的文件（如 Build 会话生成的 HTML 报告）。
    /// </summary>
    internal static bool ShouldRollBackForValidation(BuildValidationStatus status)
        => status == BuildValidationStatus.Failed;

    /// <summary>
    /// 预算 100% 消耗时的用户询问（复用工具审批管道）：经 ApprovalRequestEvent 弹窗，
    /// 决策语义映射 AllowOnce=追加预算继续 / AllowAlways=收尾总结 / Deny=直接停止。
    /// 选项说明写入 Prompt 字段，由 TUI 审批组件原样呈现。
    /// </summary>
    private static async Task<ApprovalDecision> RequestBudgetDecisionAsync(
        System.Threading.Channels.ChannelWriter<object> writer,
        long usedTokens,
        long limitTokens,
        CancellationToken ct)
    {
        var evt = new Query.ApprovalRequestEvent(
            RequestId: Guid.NewGuid().ToString("N"),
            ToolName: "预算已达上限",
            ToolInput: $"可计费 token 已消耗 {usedTokens:N0}，达到本次执行上限 {limitTokens:N0}",
            Prompt: "allow_once=追加预算继续（上限加倍） · allow_always=收尾总结 · deny=直接停止");
        await writer.WriteAsync(evt, ct).ConfigureAwait(false);
        return await evt.ResponseSource.Task.WaitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 流式零输出的重试判定：仅当 run 尚未重试过、且产出完全为零（零文本零轮次零工具调用）、
    /// 且终因为输出上限（Length）时才重试。已有工具调用的 run 绝不重试——重放有副作用的
    /// run 会重复执行工具；其他终因（Stop/取消/错误）重跑也不会改变结果。
    /// </summary>
    internal static bool ShouldRetryZeroOutputLength(
        bool alreadyRetried, int turnCount, int toolCallCount, ChatFinishReason? finishReason)
        => !alreadyRetried
            && turnCount == 0
            && toolCallCount == 0
            && finishReason == ChatFinishReason.Length;

    internal static ChatOptions BuildChatOptions(MainAgentRunOptions options)
    {
        // 开启扩展思考时不能再用低默认上限：Anthropic 要求 budget_tokens < max_tokens，
        // 显式设了 MaxOutputTokens 时适配器只能把 budget 压到 max_tokens - 1，输出预算会被吃光。
        // 留空则由适配器按 budget 自行抬高 max_tokens。
        var reasoningEffort = ResolveReasoningEffort(options);

        // 未开思考时的默认上限取升级值而非 4096：推理模型不经 Reasoning 设置也会自行推理
        // （o 系 / gpt-5 默认推理消耗输出预算），4K 会被 reasoning 全部吃光触发零输出重试
        // （受控 Build 即此案例——首轮空转 65 秒）。显式 MaxOutputTokens 仍优先。
        var chatOptions = new ChatOptions
        {
            ModelId = options.ModelId,
            MaxOutputTokens = reasoningEffort is null
                ? options.MaxOutputTokens ?? EscalatedZeroOutputRetryMaxTokens
                : options.MaxOutputTokens,
            // 主体正文走 MAF 的 agent-instructions 入口，而不是自己拼一条 system 消息：
            // HarnessAgent 会把 harness 片段拼在它前面（片段缺失时用 MAF 默认文案）。
            Instructions = string.IsNullOrEmpty(options.SystemPrompt) ? null : options.SystemPrompt,
        };

        // 思考意图走 MEAI 标准 ChatOptions.Reasoning，由各 provider 适配器翻译成自家参数。
        // 不要往 AdditionalProperties 里塞 provider 私有 wire key：那不是任何 SDK 的契约。
        if (reasoningEffort is { } effort)
            chatOptions.Reasoning = new ReasoningOptions { Effort = effort };

        // Tool approval wrapping is centralized in AgentPipelineBuilder from
        // ToolMetadataRegistry. Keeping it there prevents Main/Worker policy drift.
        if (options.Tools is { Count: > 0 })
        {
            chatOptions.Tools = options.Tools.ToList();
            chatOptions.ToolMode = ChatToolMode.Auto;
        }

        return chatOptions;
    }

    /// <summary>
    /// 解析本轮的思考档位。未开启思考返回 <c>null</c>（不设置 <see cref="ChatOptions.Reasoning"/>，
    /// 让 provider 使用自身默认值）；显式 effort 字符串优先于 budget 推导。
    /// </summary>
    private static ReasoningEffort? ResolveReasoningEffort(MainAgentRunOptions options)
    {
        if (!options.EnableThinking)
            return null;

        if (!string.IsNullOrEmpty(options.ThinkingEffort))
            return ParseReasoningEffort(options.ThinkingEffort);

        if (options.ThinkingBudgetTokens > 0)
            return EffortThinking.ToReasoningEffort(options.ThinkingBudgetTokens);

        return null;
    }

    private static ReasoningEffort? ParseReasoningEffort(string effort) => effort.ToLowerInvariant() switch
    {
        "none" or "disabled" or "off" => ReasoningEffort.None,
        "low" => ReasoningEffort.Low,
        "medium" => ReasoningEffort.Medium,
        "high" => ReasoningEffort.High,
        "max" or "extrahigh" => ReasoningEffort.ExtraHigh,
        _ => null,
    };

    internal static List<ChatMessage> BuildMessages(MainAgentRunOptions options)
    {
        List<ChatMessage> messages = [];

        // 正文已由 BuildChatOptions 交给 ChatOptions.Instructions，这里不能再加一条 system
        // 消息，否则同一段正文会既作指令又作对话消息重复送达模型。
        if (options.Messages is { Count: > 0 })
            messages.AddRange(options.Messages);

        // Prefer pre-built UserMessage (may contain multimodal content) over plain text.
        if (options.UserMessage is not null)
            messages.Add(options.UserMessage);
        else if (!string.IsNullOrEmpty(options.UserPrompt))
            messages.Add(new ChatMessage(ChatRole.User, options.UserPrompt));

        return messages;
    }
}
