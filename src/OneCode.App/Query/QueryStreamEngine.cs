using OneCode.Core.Config;
using Microsoft.Extensions.AI;
using OneCode.App.Services;
using OneCode.App.Services.Agent;
using OneCode.App.Services.BuildMode;
using OneCode.App.Services.Compact;
using OneCode.App.Services.Notifier;
using OneCode.App.Services.Observability;
using OneCode.App.Session;
using OneCode.Core.Build;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace OneCode.App.Query;

/// <summary>
/// Streaming orchestration core for interactive chat and workflow runs: Build 门禁前置
/// (clarification → plan approval → durable attempt)、MAF agent run 驱动、事件消化与终结记账。
///
/// 组合说明：<see cref="ChatService"/> 是对外契约门面；本类由其在构造函数内组装
/// （与 <see cref="BuildRunGate"/> 相同的组合根模式），不单独注册 DI，也绝不反向引用
/// <see cref="ChatService"/>。可变流式状态收敛在 <see cref="StreamingSession"/>。
/// </summary>
internal sealed class QueryStreamEngine
{
    private readonly ILogger _logger;
    private readonly IMainAgentRunner _mainAgentRunner;
    private readonly IToolCatalog _toolCatalog;
    private readonly IHookExecutionService _hookExecutionService;
    private readonly ISessionManager _sessionManager;
    private readonly ITokenUsageTracker _tokenUsageTracker;
    private readonly ITokenBreakdownEstimator _tokenBreakdownEstimator;
    private readonly IConfigManager _configManager;
    private readonly INotifierService _notifierService;
    private readonly ISessionToolSetManager _sessionToolSetManager;
    private readonly IToolCapabilityResolver _toolCapabilityResolver;
    private readonly BuildRunGate _buildRunGate;
    private readonly TranscriptPersistence _transcriptPersistence;
    private readonly ToolAssembler _toolAssembler;
    private readonly BuildPreambleRunner _buildPreambleRunner;
    private readonly HookDispatcher _hookDispatcher;

    /// <summary>Latest cache-safe snapshot for sub-agent spawning; owned here so the facade can delegate.</summary>
    public CacheSafeParams? LastCacheSafeParams { get; private set; }

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
        ChatObservabilityDependencies observability)
    {
        _logger = logger;
        _mainAgentRunner = mainAgentRunner;
        _toolCatalog = toolCatalog;
        _hookExecutionService = hookExecutionService;
        _sessionManager = session.SessionManager;
        _sessionToolSetManager = session.SessionToolSetManager;
        _toolCapabilityResolver = session.ToolCapabilityResolver;
        _configManager = session.ConfigManager;
        _buildRunGate = new BuildRunGate(
            mainAgentRunner,
            session.SessionManager,
            session.ToolProtocolValidator ?? new ToolProtocolValidator(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<BuildRunGate>.Instance,
            session.BuildRunCoordinator,
            session.ClarificationInteraction,
            session.ControlledBuildAttemptHost,
            session.BuildRunStore,
            session.OperationLedger,
            session.PlanWorkflow,
            session.PlanCardPublisher);
        _tokenUsageTracker = observability.TokenUsageTracker;
        _tokenBreakdownEstimator = observability.TokenBreakdownEstimator;
        _notifierService = observability.NotifierService;
        _transcriptPersistence = new TranscriptPersistence(session.SessionManager, logger);
        _toolAssembler = new ToolAssembler(
            _toolCatalog, _configManager, _sessionToolSetManager, _toolCapabilityResolver, _logger);
        _buildPreambleRunner = new BuildPreambleRunner(_buildRunGate, _toolAssembler);
        _hookDispatcher = new HookDispatcher(_hookExecutionService, _configManager, _notifierService);
    }

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
        string? harnessInstructions = null)
    {
        yield return new ToolPoolReadyEvent(0, 0, 0);
        var includeNextPrompt = _configManager.Current.Effective.NextPromptSuggesterEnabled == true;
        if (includeNextPrompt)
            systemPrompt += NextPromptTrailerInstruction;

        var lastUserMessage = messages.LastOrDefault(m => m.Role == ChatRole.User);
        var userPrompt = lastUserMessage?.Text ?? "";
        var isMultimodal = lastUserMessage?.Contents?.OfType<DataContent>().Any() == true;
        workingDirectory ??= _sessionManager.WorkingDirectory ?? Environment.CurrentDirectory;
        sessionId ??= _sessionManager.ForegroundConversation?.Id;
        var conversationId = _sessionManager.ForegroundConversation?.Id;

        // input 拦截点：在 AppendUserPromptAsync 之前触发——被阻断的 prompt 不进入运行循环、
        // 也不落会话历史（deny 阻断；AdditionalContexts 以 user 消息并入本轮输入）。
        var promptHook = await _hookDispatcher.FireHookAsync(
            HookInterceptionPoint.Input,
            sessionId,
            workingDirectory,
            ct,
            configure: p => p.UserMessage = userPrompt).ConfigureAwait(false);
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
            HarnessInstructions: harnessInstructions);

        await foreach (var e in QueryStreamHelpers.WithActivationContextAsync(
            conversationId?.ToString(), capabilities, agentRunId,
            () => StreamCoreAsync(request, ct), ct).ConfigureAwait(false))
        {
            yield return e;
        }
    }

    /// <summary>
    /// Starts an application-orchestrated run with an explicit context boundary.
    /// It does not append a synthetic user message and does not replay the planning transcript.
    /// </summary>
    internal async IAsyncEnumerable<QueryEvent> StreamWorkflowAsync(
        WorkflowRunRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var capabilities = _toolCapabilityResolver.Resolve(request.WorkingMode);
        var localTools = _toolAssembler.AssembleTools(request.Instruction, capabilities, request.SessionId);

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
            HarnessInstructions: request.HarnessInstructions);

        await foreach (var e in QueryStreamHelpers.WithActivationContextAsync(
            request.SessionId.ToString(), capabilities, request.RunId,
            () => StreamCoreAsync(streamRequest, ct), ct).ConfigureAwait(false))
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

    private async IAsyncEnumerable<QueryEvent> StreamCoreAsync(
        QueryStreamRequest request,
        [EnumeratorCancellation] CancellationToken ct)
    {
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

        var previousBuildRunId = OneCodeAgentRunContext.CurrentBuildRunId;
        OneCodeAgentRunContext.CurrentBuildRunId = preambleState.BuildRun?.Id.ToString();

        var aggregatedText = new System.Text.StringBuilder();
        var aggregatedTurns = 0;
        TokenUsage? aggregatedUsage = null;
        StreamingSession session = null!;
        var outcome = new TerminalOutcomeState { Reason = RunTerminalReason.Completed };
        try
        {
            // 单轮执行：output 是唯一的最终响应门；Stop hook 已退出 Hook 协议。
            session = new StreamingSession(
                request.AgentRunId,
                request.IncludeNextPrompt,
                _logger,
                name => _toolAssembler.TryAutoActivateUnknownTool(name, request.LocalTools));
            var options = BuildAgentRunOptions(request);
            var channel = Channel.CreateUnbounded<object>();
            var runTask = StartRun(
                preambleState.BuildRun, options, request.LocalTools, channel.Writer, useDurableBuildAttempt, ct);
            try
            {
                await foreach (var evt in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                {
                    foreach (var e in session.Digest(evt))
                        yield return e;
                }
            }
            finally
            {
                if (ct.IsCancellationRequested)
                {
                    await _buildRunGate.PersistCancelledRunAsync(
                        request.ConversationId,
                        request.AgentRunId,
                        request.WorkingMode,
                        session.ToolBatchCollector,
                        CancellationToken.None).ConfigureAwait(false);
                }
                OneCodeAgentRunContext.CurrentBuildRunId = previousBuildRunId;
            }
            foreach (var e in session.FlushTrailingText())
                yield return e;
            if (session.CompleteTurnIfStarted() is { } turnCompleted)
                yield return turnCompleted;
            var (runException, runResult) = await AwaitRunAsync(runTask).ConfigureAwait(false);
            if (runException is not null)
            {
                await OnRunFailedAsync(request, runException, ct).ConfigureAwait(false);
                yield return new ErrorEvent(runException.Message);
                yield break;
            }
            aggregatedText.Append(session.FinalText);
            aggregatedTurns += session.TurnCount;
            aggregatedUsage = QueryStreamHelpers.SumUsage(aggregatedUsage, session.FinalUsage);
            await _transcriptPersistence.PersistAsync(
                request, session, session.FinalText, session.FinalUsage, ct).ConfigureAwait(false);
            outcome = QueryStreamHelpers.ResolveTerminalOutcome(session, options, runResult, session.FinalText);
            // output 拦截点：最终响应交付调用方前的策略门。终因只用于产品收尾，不作 hook matcher。
            // deny 拦截本次响应并终结本轮——纠偏、预算终结与异常收尾都不是该节点的职责。
            var outputResult = await _hookDispatcher.FireHookAsync(
                HookInterceptionPoint.Output,
                request.SessionId,
                request.WorkingDirectory,
                ct,
                configure: p => p.OutputText = session.FinalText).ConfigureAwait(false);
            if (outputResult?.BlockingErrors is { Count: > 0 } outputBlocks)
            {
                _logger.LogInformation("output hook blocked final response: {Error}", outputBlocks[0].Error);
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
        }
        finally
        {
            OneCodeAgentRunContext.CurrentBuildRunId = previousBuildRunId;
        }
    }

    private MainAgentRunOptions BuildAgentRunOptions(QueryStreamRequest request)
    {
        return new MainAgentRunOptions
        {
            ModelId = request.ModelId,
            SystemPrompt = request.SystemPrompt,
            // Harness fragment travels as its own MAF input: HarnessAgent composes it ahead of
            // SystemPrompt. Null here means MAF's default instructions, which is intentional for
            // paths without the product fragment (AutoDream); an empty string would suppress them.
            HarnessInstructions = request.HarnessInstructions,
            UserPrompt = request.UserPrompt,
            UserMessage = request.IsMultimodal ? request.LastUserMessage : null,
            Messages = request.RunInputMessages,
            WorkingDirectory = request.WorkingDirectory,
            // 优先从 IConfigManager.Current.Effective.MaxTurns 动态读取（支持运行时 /config 修改），
            // 回退到构造函数参数。
            MaxTurns = _toolAssembler.ResolveMaxTurns(),
            EnableThinking = request.ThinkingBudget > 0,
            ThinkingBudgetTokens = request.ThinkingBudget ?? 0,
            Tools = request.LocalTools.Cast<AITool>().ToList(),
            ToolCapabilities = ToolActivationContext.CurrentCapabilities,
            WorkingMode = request.WorkingMode,
            FileChangeCallback = request.FileChangeCallback,
            ConversationId = request.ConversationId,
            AgentRunId = request.AgentRunId,
            // 从 IConfigManager.Current.Effective.MaxBudgetTokens 动态读取（支持运行时 /config 修改）。
            MaxBudgetTokens = _toolAssembler.ResolveMaxBudgetTokens(),
        };
    }

    private Task<MainAgentRunResult> StartRun(
        BuildRun? buildRun,
        MainAgentRunOptions options,
        IReadOnlyList<AIFunction> localTools,
        ChannelWriter<object> eventWriter,
        bool useDurableBuildAttempt,
        CancellationToken ct)
        => useDurableBuildAttempt
            ? _buildRunGate.RunControlledBuildAttemptAsync(buildRun!, options, localTools, eventWriter, ct)
            : _mainAgentRunner.RunStreamingAsync(options, eventWriter, ct);

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
            _logger.LogWarning(ex, "Agent run failed");
            return (ex, null);
        }
    }

    /// <summary>
    /// 运行异常收场：桌面通知。异常分类与告警属可观测性平面，不进入 Hook 拦截点。
    /// </summary>
    private async Task OnRunFailedAsync(QueryStreamRequest request, Exception runException, CancellationToken ct)
    {
        await _hookDispatcher.NotifyAsync("OneCode 任务执行失败", runException.Message, ct).ConfigureAwait(false);
    }

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
        await _buildRunGate.CompletePlanRunIfPendingAsync(
            request.ConversationId,
            request.AgentRunId,
            request.WorkingMode,
            session.ToolBatchCollector.HasOpenBatch,
            ct).ConfigureAwait(false);

        await _hookDispatcher.NotifyAsync(
            "OneCode 任务执行完成",
            finalText.Length > 200 ? finalText[..200] + "…" : finalText,
            ct).ConfigureAwait(false);

        RecordUsage(request, session, finalUsage, request.LocalTools);

        await foreach (var e in ResolveDurableOutcomeAsync(request, buildRun, outcome, finalText, ct).ConfigureAwait(false))
            yield return e;

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
            request.SystemPrompt, toolsForBreakdown, messagesForBreakdown, session.TotalInputTokens);
        _tokenUsageTracker.Record(finalUsage, breakdown);
    }

    /// <summary>
    /// Reloads the durable BuildRun after its controlled attempt — the aggregate is the
    /// authority for terminal reason / rollback / validation — and emits its completion event.
    /// </summary>
    private async IAsyncEnumerable<QueryEvent> ResolveDurableOutcomeAsync(
        QueryStreamRequest request,
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

    private void UpdateCacheSafeParams(QueryStreamRequest request, IReadOnlyList<AIFunction> localTools)
    {
        // 使用 localTools 的冻结快照——子代理通过 CacheSafeParams.Tools 获取工具列表，
        // 必须是独立副本而非共享可变引用（SessionToolSet 在后续轮次可能继续追加工具）。
        var toolList = localTools.Cast<AITool>().ToList();

        LastCacheSafeParams = new CacheSafeParams
        {
            SystemPrompt = request.SystemPrompt,
            // HarnessInstructions is deliberately absent: the fragment is injected by MAF at agent
            // build time for every agent, so a forked child that reuses the parent's product fragment
            // (TeamAgentFactory / ForkedAgentRunner read it directly) must not also receive it here.
            ModelId = request.ModelId,
            ThinkingBudget = request.ThinkingBudget,
            Tools = toolList.Count > 0 ? toolList : null,
            ToolCapabilities = ToolActivationContext.CurrentCapabilities,
            Metadata = new Dictionary<string, object?> { ["turn"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds() }
        };
    }
}
