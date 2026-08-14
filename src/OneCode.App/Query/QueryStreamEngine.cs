using Microsoft.Extensions.AI;
using OneCode.App.Services;
using OneCode.App.Services.Agent;
using OneCode.App.Services.BuildMode;
using OneCode.App.Services.Compact;
using OneCode.App.Services.Notifier;
using OneCode.App.Services.Observability;
using OneCode.App.Session;
using OneCode.App.Tui;
using OneCode.Core.Build;
using OneCode.Infrastructure.Config;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace OneCode.App.Query;

/// <summary>
/// Streaming orchestration core for interactive chat and workflow runs: Build 门禁前置
/// (clarification → plan approval → durable attempt)、MAF agent run 驱动、事件消化与终结记账。
///
/// 组合说明（ADR 0006）：<see cref="ChatService"/> 是对外契约门面；本类由其在构造函数内组装
/// （与 <see cref="BuildRunGate"/> 相同的组合根模式），不单独注册 DI，也绝不反向引用
/// <see cref="ChatService"/>。可变流式状态收敛在 <see cref="StreamingSession"/>。
/// </summary>
internal sealed partial class QueryStreamEngine
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
        Action<FileChange>? fileChangeCallback = null)
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

        var historyMessages = conversationId is { } activeConversationId
            ? await LoadHistoryAsync(activeConversationId, userPrompt, ct).ConfigureAwait(false)
            : null;

        var capabilities = _toolCapabilityResolver.Resolve(workingMode);
        var localTools = AssembleTools(userPrompt, capabilities, conversationId);

        var agentRunId = Guid.NewGuid().ToString("N");
        var request = new QueryStreamRequest(
            systemPrompt, modelId, thinkingBudget, sessionId, workingDirectory,
            conversationId, userPrompt, isMultimodal, lastUserMessage, historyMessages,
            includeNextPrompt, localTools, agentRunId,
            ControlledExecution: false,
            WorkingMode: workingMode,
            FileChangeCallback: fileChangeCallback);

        await foreach (var e in WithActivationContextAsync(
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
        var localTools = AssembleTools(request.Instruction, capabilities, request.SessionId);

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
            HistoryMessages: null,
            IncludeNextPrompt: false,
            LocalTools: localTools,
            AgentRunId: request.RunId,
            ControlledExecution: true,
            WorkingMode: request.WorkingMode,
            FileChangeCallback: null,
            PrescribedBuildPlan: request.PrescribedBuildPlan);

        await foreach (var e in WithActivationContextAsync(
            request.SessionId.ToString(), capabilities, request.RunId,
            () => StreamCoreAsync(streamRequest, ct), ct).ConfigureAwait(false))
        {
            yield return e;
        }
    }

    private async Task<IReadOnlyList<ChatMessage>?> LoadHistoryAsync(
        SessionId conversationId,
        string userPrompt,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(userPrompt))
            return null;

        await _sessionManager.AppendUserMessageAsync(conversationId, userPrompt, ct)
            .ConfigureAwait(false);
        return AgentEventDigester.BuildHistoryWithoutLatestUser(
            _sessionManager.GetChatHistory(conversationId),
            userPrompt);
    }

    /// <summary>
    /// Sets the ambient activation context around a deferred stream so ToolSearch/动态激活
    /// only sees the current run's capability boundary, restoring previous values in finally —
    /// cancellation, error yield-break and consumer abandonment all skip the success tail.
    /// </summary>
    private static async IAsyncEnumerable<QueryEvent> WithActivationContextAsync(
        string? sessionKey,
        ToolCapabilitySet capabilities,
        string runId,
        Func<IAsyncEnumerable<QueryEvent>> streamFactory,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var previousConversationId = ToolActivationContext.CurrentConversationId;
        var previousCapabilities = ToolActivationContext.CurrentCapabilities;
        var previousRunId = OneCodeAgentRunContext.CurrentRunId;
        ToolActivationContext.CurrentConversationId = sessionKey;
        ToolActivationContext.CurrentCapabilities = capabilities;
        OneCodeAgentRunContext.CurrentRunId = runId;
        try
        {
            await foreach (var item in streamFactory().ConfigureAwait(false))
                yield return item;
        }
        finally
        {
            OneCodeAgentRunContext.CurrentRunId = previousRunId;
            ToolActivationContext.CurrentCapabilities = previousCapabilities;
            ToolActivationContext.CurrentConversationId = previousConversationId;
        }
    }

    private async IAsyncEnumerable<QueryEvent> StreamCoreAsync(
        QueryStreamRequest request,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var preambleState = new BuildPreambleState();
        await foreach (var gateEvent in EnsureBuildRunPreambleAsync(request, preambleState, ct).ConfigureAwait(false))
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
        var channel = Channel.CreateUnbounded<object>();

        try
        {
            var session = new StreamingSession(
                request.AgentRunId,
                request.IncludeNextPrompt,
                _logger,
                name => TryAutoActivateUnknownTool(name, request.LocalTools));
            var options = BuildAgentRunOptions(request);
            var runTask = StartRun(preambleState.BuildRun, options, request.LocalTools, channel.Writer, useDurableBuildAttempt, ct);

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

            await foreach (var e in FinalizeAsync(
                request, session, options, runResult, preambleState.BuildRun, ct).ConfigureAwait(false))
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
            UserPrompt = request.UserPrompt,
            UserMessage = request.IsMultimodal ? request.LastUserMessage : null,
            Messages = request.HistoryMessages,
            WorkingDirectory = request.WorkingDirectory,
            // 优先从 IConfigManager.Current.Effective.MaxTurns 动态读取（支持运行时 /config 修改），
            // 回退到构造函数参数。
            MaxTurns = ResolveMaxTurns(),
            EnableThinking = request.ThinkingBudget > 0,
            ThinkingBudgetTokens = request.ThinkingBudget ?? 0,
            Tools = request.LocalTools.Cast<AITool>().ToList(),
            ToolCapabilities = ToolActivationContext.CurrentCapabilities,
            WorkingMode = request.WorkingMode,
            FileChangeCallback = request.FileChangeCallback,
            ConversationId = request.ConversationId,
            AgentRunId = request.AgentRunId,
            // 从 IConfigManager.Current.Effective.MaxBudgetUsd 动态读取。
            // AppSettings.MaxBudgetUsd 是 double，MainAgentRunOptions.MaxBudgetUsd 是 decimal?，需转换。
            MaxBudgetUsd = ResolveMaxBudgetUsd(),
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

    /// <summary>Catch non-cancellation failures so StopFailure can fire; cancellation propagates.</summary>
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

    private async Task OnRunFailedAsync(QueryStreamRequest request, Exception runException, CancellationToken ct)
    {
        await FireHookAsync(HookEvent.StopFailure, request.SessionId, request.WorkingDirectory, ct);
        await NotifyAsync("OneCode 任务执行失败", runException.Message, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 终结段：transcript 持久化 → Plan run 收尾 → Stop hook/通知 → token 记账 →
    /// 终因解析（含 durable BuildRun 回读）→ DoneEvent → CacheSafe 快照更新。
    /// </summary>
    private async IAsyncEnumerable<QueryEvent> FinalizeAsync(
        QueryStreamRequest request,
        StreamingSession session,
        MainAgentRunOptions options,
        MainAgentRunResult? runResult,
        BuildRun? buildRun,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var finalText = session.FinalText;
        var finalUsage = session.FinalUsage;

        await PersistTranscriptAsync(request, session, finalText, finalUsage, ct).ConfigureAwait(false);

        await _buildRunGate.CompletePlanRunIfPendingAsync(
            request.ConversationId,
            request.AgentRunId,
            request.WorkingMode,
            session.ToolBatchCollector.HasOpenBatch,
            ct).ConfigureAwait(false);

        // Fire Stop hook before completing
        await FireHookAsync(HookEvent.Stop, request.SessionId, request.WorkingDirectory, ct);
        await NotifyAsync(
            "OneCode 任务执行完成",
            finalText.Length > 200 ? finalText[..200] + "…" : finalText,
            ct).ConfigureAwait(false);

        RecordUsage(request, session, finalUsage, request.LocalTools);

        var outcome = ResolveTerminalOutcome(session, options, runResult, finalText);
        await foreach (var e in ResolveDurableOutcomeAsync(request, buildRun, outcome, finalText, ct).ConfigureAwait(false))
            yield return e;

        yield return new DoneEvent(
            finalText,
            finalUsage,
            session.TurnCount,
            outcome.Reason,
            request.ConversationId,
            outcome.TransactionRolledBack,
            outcome.ValidationFailureSummary);

        UpdateCacheSafeParams(request, request.LocalTools);
    }

    private async Task PersistTranscriptAsync(
        QueryStreamRequest request,
        StreamingSession session,
        string finalText,
        TokenUsage finalUsage,
        CancellationToken ct)
    {
        try
        {
            if (request.ConversationId is { } completedConversationId)
            {
                if (session.ToolBatchCollector.CompletedBatches.Count > 0)
                {
                    await _sessionManager.AppendCompletedToolBatchesAsync(
                            completedConversationId,
                            session.ToolBatchCollector.CompletedBatches,
                            ct)
                        .ConfigureAwait(false);
                }

                if (session.ToolBatchCollector.HasOpenBatch)
                {
                    _logger.LogWarning(
                        "Dropping incomplete tool batch for conversation {SessionId}, run {RunId}",
                        completedConversationId,
                        request.AgentRunId);
                }

                await _sessionManager.AppendAssistantMessageAsync(
                        completedConversationId,
                        finalText,
                        finalUsage,
                        ct)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist assistant transcript for session {SessionId}", request.SessionId);
        }
    }

    private void RecordUsage(
        QueryStreamRequest request,
        StreamingSession session,
        TokenUsage finalUsage,
        IReadOnlyList<AIFunction> localTools)
    {
        // 记录 token 使用量和分场景估算到 TokenUsageTracker
        var toolsForBreakdown = localTools.ToList();
        var messagesForBreakdown = request.HistoryMessages ?? Array.Empty<ChatMessage>();
        var breakdown = _tokenBreakdownEstimator.Estimate(
            request.SystemPrompt, toolsForBreakdown, messagesForBreakdown, session.TotalInputTokens);
        _tokenUsageTracker.Record(finalUsage, breakdown);
    }

    private static TerminalOutcomeState ResolveTerminalOutcome(
        StreamingSession session,
        MainAgentRunOptions options,
        MainAgentRunResult? runResult,
        string finalText)
    {
        // Compute real terminal reason: combine runner result with turn-limit detection.
        var outcome = new TerminalOutcomeState
        {
            Reason = runResult?.TerminalReason ?? BuildTerminalReason.Completed,
            TransactionRolledBack = runResult?.TransactionRolledBack ?? false,
            ValidationFailureSummary = runResult?.ValidationFailureSummary,
        };

        // If the agent didn't explicitly signal a terminal reason, check turn limit.
        if (outcome.Reason == BuildTerminalReason.Completed && session.TurnCount >= options.MaxTurns)
            outcome.Reason = BuildTerminalReason.TurnLimitReached;

        // Detect budget exceeded from final text (BudgetGuard middleware short-circuits with a text marker).
        if (outcome.Reason == BuildTerminalReason.Completed
            && finalText.Contains("[Budget Exceeded]", StringComparison.OrdinalIgnoreCase))
        {
            outcome.Reason = BuildTerminalReason.BudgetExceeded;
        }

        return outcome;
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
}

/// <summary>
/// Parameter object for one streaming run through <see cref="QueryStreamEngine.StreamCoreAsync"/>
/// — converges the former 17-parameter core signature into named, self-documenting fields.
/// </summary>
internal sealed record QueryStreamRequest(
    string SystemPrompt,
    string ModelId,
    int? ThinkingBudget,
    SessionId? SessionId,
    string? WorkingDirectory,
    SessionId? ConversationId,
    string UserPrompt,
    bool IsMultimodal,
    ChatMessage? LastUserMessage,
    IReadOnlyList<ChatMessage>? HistoryMessages,
    bool IncludeNextPrompt,
    IReadOnlyList<AIFunction> LocalTools,
    string AgentRunId,
    bool ControlledExecution,
    WorkingMode WorkingMode,
    Action<FileChange>? FileChangeCallback,
    BuildPlan? PrescribedBuildPlan = null);

/// <summary>Mutable terminal-reason carrier rewritten by the durable BuildRun reload.</summary>
internal sealed class TerminalOutcomeState
{
    public required BuildTerminalReason Reason { get; set; }

    public bool TransactionRolledBack { get; set; }

    public string? ValidationFailureSummary { get; set; }
}
