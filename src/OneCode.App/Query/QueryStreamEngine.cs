using OneCode.Core.Config;
using Microsoft.Extensions.AI;
using OneCode.App.Services;
using OneCode.App.Services.Agent;
using OneCode.App.Services.BuildMode;
using OneCode.App.Services.Observability;
using OneCode.App.Services.PlanMode;
using OneCode.App.Session;
using OneCode.Core.Build;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace OneCode.App.Query;

/// <summary>
/// Streaming orchestration core for interactive chat and workflow runs: Build 门禁前置
/// (clarification → plan approval → durable attempt)、MAF agent run 驱动、事件消化与终结记账。
///
/// 组合说明：本类是查询领域的对外实现，直接以 <see cref="IConversationRunner"/> +
/// <see cref="ICacheSafeParamsProvider"/> 暴露给 TUI 与 headless 触发器（Cron/AutoDream），
/// 接口化消费保证构造图保持 DAG（工具实例反向依赖本接口的消费者，若以具体类型互引，
/// 构造期 DI 会无限递归）。可变流式状态收敛在 <see cref="StreamingSession"/>。
/// </summary>
public sealed class QueryStreamEngine : IConversationRunner, ICacheSafeParamsProvider
{
    private readonly ILogger _logger;
    private readonly IMainAgentRunner _mainAgentRunner;
    private readonly ISessionManager _sessionManager;
    private readonly ITokenUsageTracker _tokenUsageTracker;
    private readonly ITokenBreakdownEstimator _tokenBreakdownEstimator;
    private readonly IConfigManager _configManager;
    private readonly IToolCapabilityResolver _toolCapabilityResolver;
    private readonly BuildRunGate _buildRunGate;
    private readonly TranscriptPersistence _transcriptPersistence;
    private readonly ToolAssembler _toolAssembler;
    private readonly BuildPreambleRunner _buildPreambleRunner;
    private readonly HookDispatcher _hookDispatcher;
    private readonly OneCode.Infrastructure.Agent.IHyperlightRuntimeProbe? _hyperlightRuntimeProbe;
    private readonly OneCode.App.Services.AutoDream.IAutoDreamService? _autoDreamService;

    /// <summary>Hyperlight 不可用提示只发一次（进程生命周期）。</summary>
    private bool _hyperlightUnavailableNoticeShown;

    /// <summary>Latest cache-safe snapshot for sub-agent spawning.</summary>
    public CacheSafeParams? LastCacheSafeParams { get; private set; }

    /// <inheritdoc />
    public CacheSafeParams? Current => LastCacheSafeParams;

    // A compact trailer avoids a second inference solely to populate the TUI suggestion.
    // It is stripped before transcript persistence and emitted as SuggestionsEvent.
    private const string NextPromptTrailerInstruction =
        """

        After you have completed the user's request and no more tool calls are needed, append exactly one useful follow-up question in this form:
        <onecode-next-prompt>the follow-up question</onecode-next-prompt>
        Do not put this tag in tool arguments, code blocks, or intermediate responses.
        """;

    internal QueryStreamEngine(
        ILogger logger,
        IMainAgentRunner mainAgentRunner,
        IToolCatalog toolCatalog,
        IHookExecutionService hookExecutionService,
        ChatSessionDependencies session,
        ChatObservabilityDependencies observability,
        PlanRunCloser? planRunCloser = null,
        ILoggerFactory? loggerFactory = null)
    {
        _logger = logger;
        _mainAgentRunner = mainAgentRunner;
        _sessionManager = session.SessionManager;
        _toolCapabilityResolver = session.ToolCapabilityResolver;
        _configManager = session.ConfigManager;
        _buildRunGate = new BuildRunGate(
            mainAgentRunner,
            session.SessionManager,
            // 有 factory 时给 gate 一份真实日志（回滚观测依赖它），否则保持 NullLogger。
            loggerFactory?.CreateLogger<BuildRunGate>()
                ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<BuildRunGate>.Instance,
            session.BuildRunCoordinator,
            session.ControlledBuildAttemptHost,
            session.BuildRunStore,
            planRunCloser,
            loggerFactory);
        _tokenUsageTracker = observability.TokenUsageTracker;
        _tokenBreakdownEstimator = observability.TokenBreakdownEstimator;
        _transcriptPersistence = new TranscriptPersistence(session.SessionManager, logger);
        _toolAssembler = new ToolAssembler(
            toolCatalog, _configManager, session.SessionToolSetManager, _toolCapabilityResolver, _logger);
        _buildPreambleRunner = new BuildPreambleRunner(_buildRunGate, _toolAssembler);
        _hookDispatcher = new HookDispatcher(hookExecutionService, _configManager, observability.NotifierService);
        _hyperlightRuntimeProbe = session.HyperlightRuntimeProbe;
        _autoDreamService = observability.AutoDreamService;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<QueryEvent> StreamQueryAsync(
        string prompt,
        string systemPrompt,
        string modelId,
        int? thinkingBudget = null,
        [EnumeratorCancellation] CancellationToken ct = default,
        WorkingMode workingMode = WorkingMode.Build,
        Action<FileChange>? fileChangeCallback = null,
        IReadOnlyList<string>? imagePaths = null,
        string? harnessPrompt = null,
        bool unattended = false)
    {
        var userMessage = QueryStreamHelpers.BuildUserMessage(prompt, imagePaths, _logger);
        await foreach (var evt in StreamInteractiveAsync(
            [userMessage], systemPrompt, modelId, thinkingBudget,
            sessionId: null, workingDirectory: null, ct, workingMode, fileChangeCallback, harnessPrompt, unattended))
            yield return evt;
    }

    /// <inheritdoc />
    public IAsyncEnumerable<QueryEvent> StreamWorkflowRunAsync(
        WorkflowRunRequest request,
        CancellationToken ct = default)
        => StreamWorkflowAsync(request, ct);

    /// <summary>
    /// Starts an interactive streaming query — appends the user message to the session,
    /// assembles history/tools, wraps the run in the ambient activation context and
    /// delegates to <see cref="StreamCoreAsync"/>.
    /// </summary>
    internal async IAsyncEnumerable<QueryEvent> StreamInteractiveAsync(
        IList<ChatMessage> messages,
        string systemPrompt,
        string modelId,
        int? thinkingBudget = null,
        SessionId? sessionId = null,
        string? workingDirectory = null,
        [EnumeratorCancellation] CancellationToken ct = default,
        WorkingMode workingMode = WorkingMode.Build,
        Action<FileChange>? fileChangeCallback = null,
        string? harnessInstructions = null,
        bool unattended = false)
    {
        yield return new ToolPoolReadyEvent(0, 0, 0);
        var includeNextPrompt = !unattended && _configManager.Current.Effective.NextPromptSuggesterEnabled == true;
        if (includeNextPrompt)
            systemPrompt += NextPromptTrailerInstruction;

        var lastUserMessage = messages.LastOrDefault(m => m.Role == ChatRole.User);
        var userPrompt = lastUserMessage?.Text ?? "";
        var isMultimodal = lastUserMessage?.Contents?.OfType<DataContent>().Any() == true;
        workingDirectory ??= _sessionManager.WorkingDirectory ?? Environment.CurrentDirectory;
        sessionId ??= unattended ? SessionId.NewId() : _sessionManager.ForegroundConversation?.Id;
        var conversationId = unattended ? null : _sessionManager.ForegroundConversation?.Id;

        // input 拦截点：在 AppendUserPromptAsync 之前触发——被阻断的 prompt 不进入运行循环、
        // 也不落会话历史（deny 阻断；AdditionalContexts 以 user 消息并入本轮输入）。
        var promptHook = await _hookDispatcher.FireHookAsync(
            HookInterceptionPoint.Input,
            sessionId,
            workingDirectory,
            ct,
            configure: p => p with { UserMessage = userPrompt }).ConfigureAwait(false);
        if (promptHook?.BlockingErrors is { Count: > 0 } promptBlocks)
        {
            _logger.LogInformation("input hook blocked prompt: {Error}", promptBlocks[0].Error);
            yield return new ErrorEvent($"Prompt blocked by hook: {promptBlocks[0].Error}");
            yield return new DoneEvent(null, null, 0, RunTerminalReason.Completed, conversationId);
            yield break;
        }

        // 多轮历史由 TranscriptChatHistoryProvider 经 MAF 契约提供；本层只把本轮 user 消息落转录，
        // provider 读历史时排除它以避免与 RequestMessages 重复。
        if (conversationId is { } activeConversationId)
        {
            await AppendUserPromptAsync(activeConversationId, userPrompt, ct).ConfigureAwait(false);
        }

        // hook 注入的附加上下文作为本轮输入（user 角色消息，位于实际 prompt 之前）
        List<ChatMessage>? runInputMessages = null;
        if (promptHook?.AdditionalContexts is { Count: > 0 } promptContexts)
        {
            var contextText = string.Join("\n\n", promptContexts);
            runInputMessages = [new ChatMessage(ChatRole.User, contextText)];
        }

        var capabilities = _toolCapabilityResolver.Resolve(workingMode);
        var localTools = _toolAssembler.AssembleTools(userPrompt, capabilities, conversationId);

        var agentRunId = Guid.NewGuid().ToString("N");
        var request = new QueryStreamRequest(
            systemPrompt, modelId, thinkingBudget, sessionId, workingDirectory,
            conversationId, userPrompt, isMultimodal, lastUserMessage, runInputMessages,
            includeNextPrompt, localTools, agentRunId,
            ControlledExecution: false,
            WorkingMode: workingMode,
            FileChangeCallback: fileChangeCallback,
            HarnessInstructions: harnessInstructions,
            ToolCapabilities: capabilities,
            SuppressToolApproval: unattended);

        await foreach (var e in StreamCoreAsync(request, ct).ConfigureAwait(false))
        {
            yield return e;
        }
    }

    /// <summary>
    /// Starts an application-orchestrated run with an explicit context boundary.
    /// It does not append a synthetic user message and applies the approved-plan history seed boundary.
    /// </summary>
    internal async IAsyncEnumerable<QueryEvent> StreamWorkflowAsync(
        WorkflowRunRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var capabilities = _toolCapabilityResolver.Resolve(request.WorkingMode);
        var localTools = _toolAssembler.AssembleTools(request.Instruction, capabilities, request.SessionId);

        // 执行世代历史播种边界：保留 Plan 阶段的对话与只读检索结果，仅剥离边界之前的
        // SubmitPlan 工具调用对；epoch 漂移（记录后发生过 compact/clear 等结构性变更）时边界失效，回退全量。
        var seedBoundary = request.PrescribedBuildPlan is not null
            && _sessionManager.GetConversation(request.SessionId) is { } boundaryConversation
                ? HistorySeedBoundary.ReadValidated(boundaryConversation)
                : null;

        var streamRequest = new QueryStreamRequest(
            request.SystemPrompt,
            request.ModelId,
            ThinkingBudget: null,
            SessionId: request.SessionId,
            WorkingDirectory: request.WorkingDirectory ?? _sessionManager.WorkingDirectory ?? Environment.CurrentDirectory,
            ConversationId: request.SessionId,
            UserPrompt: request.Instruction,
            IsMultimodal: false,
            LastUserMessage: null,
            RunInputMessages: null,
            IncludeNextPrompt: false,
            LocalTools: localTools,
            AgentRunId: request.RunId,
            ControlledExecution: true,
            WorkingMode: request.WorkingMode,
            FileChangeCallback: null,
            PrescribedBuildPlan: request.PrescribedBuildPlan,
            HarnessInstructions: request.HarnessInstructions,
            ToolCapabilities: capabilities,
            HistorySeedBoundary: seedBoundary);

        await foreach (var e in StreamCoreAsync(streamRequest, ct).ConfigureAwait(false))
        {
            yield return e;
        }
    }

    /// <summary>把本轮 user 消息落转录；多轮历史由 <c>TranscriptChatHistoryProvider</c> 经 MAF 契约提供。</summary>
    private async Task AppendUserPromptAsync(
        SessionId conversationId,
        string userPrompt,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(userPrompt))
            return;

        await _sessionManager.AppendUserMessageAsync(conversationId, userPrompt, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Hyperlight 沙箱运行时不可用的一次性用户可见降级提示（原先静默降级，用户无从知晓
    /// execute_code 为何消失）。仅在会装载 CodeAct 的模式（非 Plan——Plan 已于装配期剔除）下提示，
    /// 进程生命周期内只发一次。probe 未注入（测试路径）视为可用，不发提示。
    /// </summary>
    private IEnumerable<QueryEvent> ConsumeHyperlightUnavailableNotice(WorkingMode workingMode)
    {
        if (_hyperlightUnavailableNoticeShown
            || workingMode == WorkingMode.Plan
            || _hyperlightRuntimeProbe?.IsAvailable() != false)
        {
            return [];
        }

        _hyperlightUnavailableNoticeShown = true;
        return
        [
            new NoticeEvent(
                "⚠ 代码执行沙箱（execute_code）在本机不可用（未启用 Windows Hypervisor Platform / KVM），已停用。" +
                "恢复方式：BIOS 开启 VT-x，并执行 dism /online /enable-feature /featurename:HypervisorPlatform /All。"),
        ];
    }

    private async IAsyncEnumerable<QueryEvent> StreamCoreAsync(
        QueryStreamRequest request,
        [EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var notice in ConsumeHyperlightUnavailableNotice(request.WorkingMode))
        {
            yield return notice;
        }

        var preambleState = new BuildPreambleState();
        await foreach (var gateEvent in _buildPreambleRunner.EnsureBuildRunPreambleAsync(request, preambleState, ct).ConfigureAwait(false))
            yield return gateEvent;
        if (preambleState.EarlyDone)
            yield break;

        var useDurableBuildAttempt = preambleState.BuildRun is not null;
        if (useDurableBuildAttempt && !_buildRunGate.IsConfigured)
        {
            throw new InvalidOperationException(
                "Controlled Build requires the durable attempt host, coordinator and BuildRun store.");
        }

        // 预先读取全部 ambient 旧值：一次 try/finally 覆盖所有 set 点，
        // 构建 run 骨架（StreamingSession/RunOptions/Channel/StartRun）抛出时不再泄漏。
        var ambientScope = AgentRunAmbientScope.CaptureCurrent();

        var aggregatedText = new System.Text.StringBuilder();
        var aggregatedTurns = 0;
        TokenUsage? aggregatedUsage = null;
        StreamingSession? session = null;
        var outcome = new TerminalOutcomeState { Reason = RunTerminalReason.Completed };
        // 逐批落盘进度：已持久化的封口批次数（run 收尾的 TranscriptPersistence 只补增量）。
        var persistedBatchCount = 0;
        // 尾段（Flush 之后）仍需使用；赋值均发生在 try 内、任何使用之前。
        MainAgentRunOptions options = null!;
        Task<MainAgentRunResult> runTask = null!;
        // Plan 收口防重入标记：正常路径收口后，外层 finally 的兜底收口跳过。
        var planRunClosed = false;
        // 单次收口包装：已收口直接跳过（收口本身对已终态工作流幂等，这里连调用都省去）。
        async Task CloseOutOnceAsync(PlanRunEnd end, CancellationToken token)
        {
            if (planRunClosed)
                return;
            planRunClosed = true;
            await CloseOutPlanRunAsync(request, session, end, token).ConfigureAwait(false);
        }
        try
        {
            // AsyncLocal 变更不跨 async iterator 的 yield 边界存续——每个 MoveNext 段都以消费方的
            // ExecutionContext 恢复，迭代器内部早期段的 set 对后续段不可见。preamble 事件或纠偏
            // NoticeEvent 的 yield 之后尤其如此。StartRun 捕获的 EC 决定工具执行时的 ambient
            // （SubmitPlan 的 run 绑定、动态激活会话定位都依赖它），故在此显式重建；先例见
            // ControlledBuildAttemptRuntime（受控路径的同款处理）。
            AgentRunAmbientScope.Apply(
                buildRunId: preambleState.BuildRun?.Id.ToString(),
                runId: request.AgentRunId,
                conversationId: request.ConversationId?.ToString(),
                capabilities: request.ToolCapabilities,
                planSubmissionSeal: request.WorkingMode == WorkingMode.Plan ? new RunSeal() : null);

            // CacheSafe 快照必须在 run 开始前就绪：AgentTool 在会话首个查询期间发起的 fork
            // 会读取 Current，等到 FinalizeAsync 再写就让首轮 fork 拿到 null（子代理工具表被
            // FilterChildTools 过滤成空）。此刻请求参数与工具集已全部冻结；UpdateCacheSafeParams
            // 显式读 request.ToolCapabilities，与 FinalizeAsync 的收尾快照同源一致；
            // LastCacheSafeParams 是普通实例属性，不受 AsyncLocal 跨 yield 边界失效的影响。
            UpdateCacheSafeParams(request, request.LocalTools);

            // 单轮执行：output 是唯一的最终响应门；Stop hook 已退出 Hook 协议。
            session = new StreamingSession(
                request.AgentRunId,
                request.IncludeNextPrompt,
                _logger,
                name => _toolAssembler.TryAutoActivateUnknownTool(name, request.LocalTools));
            options = BuildAgentRunOptions(request);
            var channel = Channel.CreateUnbounded<object>();
            // 子代理进度旁路：与 CurrentBuildRunId 同一模式——StartRun 同段注入，
            // fork 的工具执行上下文可读取该写入器上报实时进度；finally 复位。
            OneCodeAgentRunContext.SubagentEventWriter = channel.Writer;
            runTask = StartRun(
                preambleState.BuildRun, options, channel.Writer, useDurableBuildAttempt, ct);
            await foreach (var evt in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                foreach (var e in session.Digest(evt))
                    yield return e;
                persistedBatchCount = await _transcriptPersistence.PersistSealedBatchesAsync(
                    request, session, persistedBatchCount, ct).ConfigureAwait(false);
            }
            foreach (var e in session.FlushTrailingText())
                yield return e;
            if (session.CompleteTurnIfStarted() is { } turnCompleted)
                yield return turnCompleted;
            var (runException, runResult) = await AwaitRunAsync(runTask).ConfigureAwait(false);
            if (runException is not null)
            {
                await CloseOutOnceAsync(PlanRunEnd.Finished, ct).ConfigureAwait(false);
                await OnRunFailedAsync(runException, ct).ConfigureAwait(false);
                yield return new ErrorEvent(runException.Message + PromptTooLongHint(runException));
                yield break;
            }
            aggregatedText.Append(session.FinalText);
            aggregatedTurns += session.TurnCount;
            aggregatedUsage = QueryStreamHelpers.SumUsage(aggregatedUsage, session.FinalUsage);
            await _transcriptPersistence.PersistAsync(
                request, session, session.FinalText, session.FinalUsage, ct, alreadyPersistedBatchCount: persistedBatchCount).ConfigureAwait(false);
            outcome = QueryStreamHelpers.ResolveTerminalOutcome(session, options, runResult, session.FinalText);
            // output 拦截点：最终响应交付调用方前的策略门。终因只用于产品收尾，不作 hook matcher。
            // deny 拦截本次响应并终结本轮——纠偏、预算终结与异常收尾都不是该节点的职责。
            var outputResult = await _hookDispatcher.FireHookAsync(
                HookInterceptionPoint.Output,
                request.SessionId,
                request.WorkingDirectory,
                ct,
                configure: p => p with { OutputText = session.FinalText }).ConfigureAwait(false);
            if (outputResult?.BlockingErrors is { Count: > 0 } outputBlocks)
            {
                _logger.LogInformation("output hook blocked final response: {Error}", outputBlocks[0].Error);
                await CloseOutOnceAsync(PlanRunEnd.Finished, ct).ConfigureAwait(false);
                yield return new ErrorEvent($"Response blocked by hook: {outputBlocks[0].Error}");
                yield return new DoneEvent(
                    null, session.FinalUsage, session.TurnCount, RunTerminalReason.Blocked, request.ConversationId);
                yield break;
            }
            await foreach (var e in FinalizeAsync(
                request,
                session,
                preambleState.BuildRun,
                aggregatedText.ToString(),
                aggregatedUsage!,
                aggregatedTurns,
                outcome,
                ct).ConfigureAwait(false))
            {
                yield return e;
            }
            // FinalizeAsync 首行已完成收口；置位防外层兜底重复调用。
            planRunClosed = true;
        }
        finally
        {
            // 兜底收口：只要未走完正常收口路径（含 StartRun 前取消或同步异常、try 内非取消异常、
            // 取消与 AwaitRunAsync 的 OCE 竞态），都补做收口，防止工作流滞留。
            // 对已收口/非 Plan 工作流为幂等 no-op；用 None 而非 ct——取消时的批次落盘与取消写必须完成。
            if (!planRunClosed)
            {
                await CloseOutPlanRunAsync(
                    request,
                    session,
                    ct.IsCancellationRequested ? PlanRunEnd.Cancelled : PlanRunEnd.Finished,
                    CancellationToken.None).ConfigureAwait(false);
            }
            // 全量复位 ambient：消费尾段（Flush/Finalize）无 ambient 读取方，
            // 统一在外层收口，覆盖构建骨架阶段的早期异常路径。
            ambientScope.Dispose();
        }
    }

    private MainAgentRunOptions BuildAgentRunOptions(QueryStreamRequest request)
        => QueryStreamHelpers.BuildAgentRunOptions(request, _toolAssembler);

    private Task<MainAgentRunResult> StartRun(
        BuildRun? buildRun,
        MainAgentRunOptions options,
        ChannelWriter<object> eventWriter,
        bool useDurableBuildAttempt,
        CancellationToken ct)
        => useDurableBuildAttempt
            ? _buildRunGate.RunControlledBuildAttemptAsync(buildRun!, options, eventWriter, ct)
            : _mainAgentRunner.RunStreamingAsync(options, eventWriter, ct);

    private Task CloseOutPlanRunAsync(
        QueryStreamRequest request,
        StreamingSession? session,
        PlanRunEnd end,
        CancellationToken ct)
        => _buildRunGate.CloseOutPlanRunAsync(
            request.ConversationId,
            request.AgentRunId,
            request.WorkingMode,
            end,
            session?.ToolBatchCollector,
            ct);

    /// <summary>Catch non-cancellation failures so the failure notification fires; cancellation propagates.</summary>
    private async Task<(Exception? RunException, MainAgentRunResult? RunResult)> AwaitRunAsync(
        Task<MainAgentRunResult> runTask)
    {
        try
        {
            return (null, await runTask.ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // run 失败是错误事件而非告警：2026-10-12 的 NRE 事故中，崩溃链条在日志里
            // 一条 ERROR 都没有，只能靠 WARNING 与状态差异倒推。级别失真会拖慢排障。
            _logger.LogError(ex, "Agent run failed");
            return (ex, null);
        }
    }

    /// <summary>
    /// 运行异常收场：桌面通知。异常分类与告警属可观测性平面，不进入 Hook 拦截点。
    /// </summary>
    private async Task OnRunFailedAsync(Exception runException, CancellationToken ct)
    {
        await _hookDispatcher.NotifyAsync("OneCode 任务执行失败", runException.Message, ct).ConfigureAwait(false);
    }

    private static string PromptTooLongHint(Exception runException) =>
        QueryStreamHelpers.PromptTooLongHint(runException);

    /// <summary>
    /// 终结段：Plan run 收尾 → 完成通知 → token 记账 → durable BuildRun 回读 →
    /// DoneEvent → CacheSafe 快照更新。transcript 持久化与 output 拦截点已前移至
    /// <see cref="StreamCoreAsync"/>（在终结段之前逐轮执行）。
    /// </summary>
    private async IAsyncEnumerable<QueryEvent> FinalizeAsync(
        QueryStreamRequest request,
        StreamingSession session,
        BuildRun? buildRun,
        string finalText,
        TokenUsage finalUsage,
        int turnsCompleted,
        TerminalOutcomeState outcome,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await CloseOutPlanRunAsync(request, session, PlanRunEnd.Finished, ct).ConfigureAwait(false);

        await _hookDispatcher.NotifyAsync(
            "OneCode 任务执行完成",
            finalText.Length > 200 ? finalText[..200] + "…" : finalText,
            ct).ConfigureAwait(false);

        RecordUsage(request, session, finalUsage, request.LocalTools);

        await foreach (var e in ResolveDurableOutcomeAsync(buildRun, outcome, session.LastTurnText, ct).ConfigureAwait(false))
            yield return e;

        // 唯一自动记忆整合触发点：仅在 BUILD 模式（含普通交互式 Build 与 Plan 批准后转入的受控 Build）
        // 且任务正常完成、文件事务未回滚时触发后台增量记忆整合。
        if (request.WorkingMode == WorkingMode.Build
            && outcome.Reason == RunTerminalReason.Completed
            && !outcome.TransactionRolledBack)
        {
            _autoDreamService?.NotifyTaskCompleted();
        }

        yield return new DoneEvent(
            finalText,
            finalUsage,
            turnsCompleted,
            outcome.Reason,
            request.ConversationId,
            outcome.TransactionRolledBack,
            outcome.ValidationFailureSummary);

        UpdateCacheSafeParams(request, request.LocalTools);
    }

    private void RecordUsage(
        QueryStreamRequest request,
        StreamingSession session,
        TokenUsage finalUsage,
        IReadOnlyList<AIFunction> localTools)
    {
        // 记录 token 使用量和分场景估算；消息分项从转录读取（含多轮历史与本轮输入）。
        var toolsForBreakdown = localTools.ToList();
        IReadOnlyList<ChatMessage> messagesForBreakdown = request.ConversationId is { } breakdownConversationId
            ? _sessionManager.GetChatHistory(breakdownConversationId)
            : [];
        var breakdown = _tokenBreakdownEstimator.Estimate(
            request.SystemPrompt, toolsForBreakdown, messagesForBreakdown, session.LastTurnInputTokens);
        _tokenUsageTracker.Record(finalUsage, breakdown);
    }

    /// <summary>
    /// Reloads the durable BuildRun after its controlled attempt — the aggregate is the
    /// authority for terminal reason / rollback / validation — and emits its completion event.
    /// </summary>
    private async IAsyncEnumerable<QueryEvent> ResolveDurableOutcomeAsync(
        BuildRun? buildRun,
        TerminalOutcomeState outcome,
        string finalText,
        [EnumeratorCancellation] CancellationToken ct)
    {
        if (buildRun is null || _buildRunGate.BuildRunStore is not { } buildRunStore)
            yield break;

        var reloaded = await buildRunStore.LoadByIdAsync(buildRun.Id, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"BuildRun '{buildRun.Id}' disappeared after its durable attempt.");
        outcome.Reason = BuildRunGate.ResolveTerminalReason(reloaded);
        outcome.TransactionRolledBack = reloaded.TransactionRolledBack;
        outcome.ValidationFailureSummary = reloaded.FailureSummary;
        if (reloaded.State == BuildRunState.Completed)
            yield return new BuildRunCompletedEvent(BuildRunGate.CreateBuildRunResult(reloaded, finalText));
    }

    private void UpdateCacheSafeParams(QueryStreamRequest request, IReadOnlyList<AIFunction> localTools) =>
        LastCacheSafeParams = QueryStreamHelpers.CreateCacheSafeParams(request, localTools);
}
