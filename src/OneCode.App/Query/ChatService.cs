using OneCode.App.Services;
using OneCode.App.Services.Agent;
using OneCode.App.Services.BuildMode;
using OneCode.App.Services.Compact;
using OneCode.App.Services.Notifier;
using OneCode.App.Services.Observability;
using OneCode.App.Services.PlanMode;
using OneCode.App.Session;
using OneCode.App.Tui;
using OneCode.Core.Build;
using OneCode.Core.Models;
using OneCode.Core.PlanMode;
using OneCode.Infrastructure.Agent;
using OneCode.Infrastructure.Config;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace OneCode.App.Query;

/// <summary>
/// Chat service — delegates agentic loop to MAF via MainAgentRunner.
/// Preserves IAsyncEnumerable&lt;QueryEvent&gt; contract for TUI and BackgroundService
/// (Cron/AutoDream) consumption.
/// </summary>
/// <remarks>
/// 实现 <see cref="IConversationRunner"/> 的原因：headless 触发器（CronJobExecutor 等）依赖接口而非
/// 本具体类，以断开 ChatService → ToolCatalog → CronCreateTool → CronSchedulerService →
/// ICronJobExecutor → ChatService 的循环依赖。
/// </remarks>
public sealed class ChatService : IConversationRunner, ICacheSafeParamsProvider
{
    private readonly IMainAgentRunner _mainAgentRunner;
    private readonly ILogger<ChatService> _logger;
    private readonly IToolCatalog _toolCatalog;
    private readonly IHookExecutionService _hookExecutionService;
    private readonly ISessionManager _sessionManager;
    private readonly ITokenUsageTracker _tokenUsageTracker;
    private readonly ITokenBreakdownEstimator _tokenBreakdownEstimator;
    private readonly IConfigManager _configManager;
    private readonly INotifierService _notifierService;
    private readonly ISessionToolSetManager _sessionToolSetManager;
    private readonly IToolCapabilityResolver _toolCapabilityResolver;
    private readonly IPlanWorkflowApplicationService? _planWorkflow;
    private readonly IBuildRunCoordinator? _buildRunCoordinator;
    private readonly IClarificationInteractionService? _clarificationInteraction;
    private readonly ControlledBuildAttemptHost? _controlledBuildAttemptHost;
    private readonly IBuildRunStore? _buildRunStore;
    private readonly OneCode.Core.Workflows.IOperationLedger? _operationLedger;
    private readonly IToolProtocolValidator _toolProtocolValidator;
    private readonly PlanCardPublisher? _planCardPublisher;
    private CacheSafeParams? _lastCacheSafeParams;

    // A compact trailer avoids a second inference solely to populate the TUI suggestion.
    // It is stripped before transcript persistence and emitted as SuggestionsEvent.
    private const string NextPromptTrailerInstruction =
        """

        After you have completed the user's request and no more tool calls are needed, append exactly one useful follow-up question in this form:
        <onecode-next-prompt>the follow-up question</onecode-next-prompt>
        Do not put this tag in tool arguments, code blocks, or intermediate responses.
        """;

    /// <inheritdoc />
    public CacheSafeParams? Current => _lastCacheSafeParams;

    /// <summary>Alias for <see cref="Current"/> kept for call sites that still read the snapshot by name.</summary>
    public CacheSafeParams? LastCacheSafeParams => _lastCacheSafeParams;

    public ChatService(
        ILogger<ChatService> logger,
        IMainAgentRunner mainAgentRunner,
        IToolCatalog toolCatalog,
        IHookExecutionService hookExecutionService,
        ChatSessionDependencies session,
        ChatObservabilityDependencies observability)
    {
        _mainAgentRunner = mainAgentRunner;
        _logger = logger;
        _toolCatalog = toolCatalog;
        _hookExecutionService = hookExecutionService;
        _sessionManager = session.SessionManager;
        _sessionToolSetManager = session.SessionToolSetManager;
        _toolCapabilityResolver = session.ToolCapabilityResolver;
        _configManager = session.ConfigManager;
        _planWorkflow = session.PlanWorkflow;
        _buildRunCoordinator = session.BuildRunCoordinator;
        _clarificationInteraction = session.ClarificationInteraction;
        _controlledBuildAttemptHost = session.ControlledBuildAttemptHost;
        _buildRunStore = session.BuildRunStore;
        _operationLedger = session.OperationLedger;
        _toolProtocolValidator = session.ToolProtocolValidator ?? new ToolProtocolValidator();
        _planCardPublisher = session.PlanCardPublisher;
        _tokenUsageTracker = observability.TokenUsageTracker;
        _tokenBreakdownEstimator = observability.TokenBreakdownEstimator;
        _notifierService = observability.NotifierService;
    }

    /// <summary>Convenience overload.</summary>
    public IAsyncEnumerable<QueryEvent> StreamQueryAsync(
        string prompt,
        string systemPrompt,
        string modelId,
        int? thinkingBudget = null,
        CancellationToken ct = default,
        WorkingMode workingMode = WorkingMode.Build,
        Action<FileChange>? fileChangeCallback = null,
        IReadOnlyList<string>? imagePaths = null)
    {
        var userMessage = BuildUserMessage(prompt, imagePaths);
        var messages = new List<ChatMessage> { userMessage };
        return StreamQueryAsync(messages, systemPrompt, modelId, thinkingBudget, null, null, ct, workingMode, fileChangeCallback);
    }

    /// <summary>
    /// Starts an application-orchestrated run with an explicit context boundary.
    /// It does not append a synthetic user message and does not replay the planning transcript.
    /// </summary>
    public async IAsyncEnumerable<QueryEvent> StreamWorkflowRunAsync(
        WorkflowRunRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var capabilities = _toolCapabilityResolver.Resolve(request.WorkingMode);
        var localTools = AssembleTools(request.Instruction, capabilities, request.SessionId);
        var previousConversationId = ToolActivationContext.CurrentConversationId;
        var previousCapabilities = ToolActivationContext.CurrentCapabilities;
        var previousRunId = OneCodeAgentRunContext.CurrentRunId;
        ToolActivationContext.CurrentConversationId = request.SessionId.ToString();
        ToolActivationContext.CurrentCapabilities = capabilities;
        OneCodeAgentRunContext.CurrentRunId = request.RunId;
        try
        {
            await foreach (var item in StreamQueryCoreAsync(
                request.SystemPrompt,
                request.ModelId,
                thinkingBudget: null,
                request.SessionId,
                request.WorkingDirectory ?? _sessionManager.WorkingDirectory ?? Environment.CurrentDirectory,
                request.SessionId,
                request.Instruction,
                isMultimodal: false,
                lastUserMessage: null,
                historyMessages: null,
                includeNextPrompt: false,
                localTools,
                request.RunId,
                controlledExecution: true,
                ct,
                request.WorkingMode,
                fileChangeCallback: null,
                request.PrescribedBuildPlan).ConfigureAwait(false))
            {
                yield return item;
            }
        }
        finally
        {
            OneCodeAgentRunContext.CurrentRunId = previousRunId;
            ToolActivationContext.CurrentCapabilities = previousCapabilities;
            ToolActivationContext.CurrentConversationId = previousConversationId;
        }
    }

    /// <summary>
    /// Builds a user <see cref="ChatMessage"/>. When <paramref name="imagePaths"/> is provided,
    /// constructs a multi-content message with text + image <see cref="DataContent"/> blocks
    /// following the MAF multimodal pattern.
    /// </summary>
    private ChatMessage BuildUserMessage(string prompt, IReadOnlyList<string>? imagePaths)
    {
        if (imagePaths is not { Count: > 0 })
            return new ChatMessage(ChatRole.User, prompt);

        var contents = new List<AIContent>();
        if (!string.IsNullOrEmpty(prompt))
            contents.Add(new TextContent(prompt));

        foreach (var path in imagePaths)
        {
            try
            {
                var bytes = File.ReadAllBytes(path);
                var ext = Path.GetExtension(path).ToLowerInvariant();
                var mediaType = ext switch
                {
                    ".jpg" or ".jpeg" => "image/jpeg",
                    ".gif" => "image/gif",
                    ".webp" => "image/webp",
                    ".bmp" => "image/bmp",
                    _ => "image/png",
                };
                contents.Add(new DataContent(bytes, mediaType));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to read image {Path}", path);
                contents.Add(new TextContent($"[Failed to load image: {Path.GetFileName(path)}]"));
            }
        }

        return new ChatMessage(ChatRole.User, contents);
    }

    /// <summary>Streaming  — delegated to MAF ChatClientAgent via MainAgentRunner.RunStreamingAsync.</summary>
    public async IAsyncEnumerable<QueryEvent> StreamQueryAsync(
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

        IReadOnlyList<ChatMessage>? historyMessages = null;
        if (conversationId is { } activeConversationId
            && !string.IsNullOrWhiteSpace(userPrompt))
        {
            await _sessionManager.AppendUserMessageAsync(activeConversationId, userPrompt, ct)
                .ConfigureAwait(false);
            historyMessages = BuildHistoryWithoutLatestUser(
                _sessionManager.GetChatHistory(activeConversationId),
                userPrompt);
        }

        var capabilities = _toolCapabilityResolver.Resolve(workingMode);
        var localTools = AssembleTools(userPrompt, capabilities, conversationId);

        // 设置 ToolActivationContext，使 ToolSearch/动态激活只能看到当前 run 的能力边界。
        // 必须在 finally 中恢复：取消 / 错误 yield break / 消费者中止都会跳过成功路径尾部。
        var sessionKey = conversationId?.ToString();
        var previousActivationContext = ToolActivationContext.CurrentConversationId;
        var previousCapabilities = ToolActivationContext.CurrentCapabilities;
        var previousRunId = OneCodeAgentRunContext.CurrentRunId;
        var agentRunId = Guid.NewGuid().ToString("N");
        ToolActivationContext.CurrentConversationId = sessionKey;
        ToolActivationContext.CurrentCapabilities = capabilities;
        OneCodeAgentRunContext.CurrentRunId = agentRunId;
        try
        {
            await foreach (var item in StreamQueryCoreAsync(
                systemPrompt, modelId, thinkingBudget, sessionId, workingDirectory,
                conversationId, userPrompt, isMultimodal, lastUserMessage, historyMessages,
                includeNextPrompt, localTools, agentRunId, controlledExecution: false, ct, workingMode, fileChangeCallback,
                prescribedBuildPlan: null).ConfigureAwait(false))
            {
                yield return item;
            }
        }
        finally
        {
            OneCodeAgentRunContext.CurrentRunId = previousRunId;
            ToolActivationContext.CurrentCapabilities = previousCapabilities;
            ToolActivationContext.CurrentConversationId = previousActivationContext;
        }
    }

    private async IAsyncEnumerable<QueryEvent> StreamQueryCoreAsync(
        string systemPrompt,
        string modelId,
        int? thinkingBudget,
        SessionId? sessionId,
        string? workingDirectory,
        SessionId? conversationId,
        string userPrompt,
        bool isMultimodal,
        ChatMessage? lastUserMessage,
        IReadOnlyList<ChatMessage>? historyMessages,
        bool includeNextPrompt,
        IReadOnlyList<AIFunction> localTools,
        string agentRunId,
        bool controlledExecution,
        [EnumeratorCancellation] CancellationToken ct,
        WorkingMode workingMode,
        Action<FileChange>? fileChangeCallback,
        BuildPlan? prescribedBuildPlan)
    {
        BuildRun? buildRun = null;
        // Direct Build conversations intentionally stay on the lightweight agent path.
        // MainAgentRunner already owns an EditTransaction and final verification for actual writes.
        // A durable BuildRun is reserved for explicit workflow execution/recovery. The caller
        // sets controlledExecution instead of relying on fragile natural-language intent keywords.
        if (controlledExecution
            && workingMode == WorkingMode.Build
            && conversationId is { } buildConversationId
            && _buildRunCoordinator is not null)
        {
            var durableStates = new List<BuildRun>();
            buildRun = await _buildRunCoordinator.BeginOrResumeAsync(
                buildConversationId,
                userPrompt,
                workingDirectory ?? Environment.CurrentDirectory,
                ct,
                durableStates.Add,
                prescribedBuildPlan).ConfigureAwait(false);
            foreach (var durableState in durableStates)
                yield return BuildRunStateEvent.From(durableState);
            if (durableStates.Count == 0 || durableStates[^1].Version != buildRun.Version)
                yield return BuildRunStateEvent.From(buildRun);

            while (buildRun.State == BuildRunState.Clarifying && _clarificationInteraction is not null)
            {
                var clarification = await _clarificationInteraction.AskAsync(
                    "开始执行前需要确认",
                    buildRun.ClarificationQuestions,
                    confirmationOnly: buildRun.ProposedScope is not null,
                    ct).ConfigureAwait(false);
                if (clarification.IsCancelled || string.IsNullOrWhiteSpace(clarification.Response))
                    break;

                durableStates.Clear();
                buildRun = await _buildRunCoordinator.BeginOrResumeAsync(
                    buildConversationId,
                    clarification.Response,
                    workingDirectory ?? Environment.CurrentDirectory,
                    ct,
                    durableStates.Add,
                    prescribedBuildPlan).ConfigureAwait(false);
                foreach (var durableState in durableStates)
                    yield return BuildRunStateEvent.From(durableState);
                if (durableStates.Count == 0 || durableStates[^1].Version != buildRun.Version)
                    yield return BuildRunStateEvent.From(buildRun);
            }

            // Plan approval gate: the generated plan + tool policy is parked in Planned until the
            // user approves it. This is a business-layer interaction (same dialog as clarification),
            // deliberately not a MAF RequestPort — the BuildRun aggregate already persists the
            // Planned state, so a crash simply re-asks on resume.
            while (buildRun.State == BuildRunState.Planned && _clarificationInteraction is not null)
            {
                var approvedTools = SnapshotApprovedTools();
                var approval = await _clarificationInteraction.AskAsync(
                    "计划已生成，请确认后开始执行",
                    [BuildPlanApprovalPrompt(buildRun, approvedTools)],
                    confirmationOnly: true,
                    ct).ConfigureAwait(false);
                buildRun = approval.IsCancelled || string.IsNullOrWhiteSpace(approval.Response)
                    ? await _buildRunCoordinator.RejectPlanAsync(
                        buildRun.Id,
                        "用户取消计划审批",
                        ct).ConfigureAwait(false)
                    : approvedTools.Count == 0
                        ? await _buildRunCoordinator.RejectPlanAsync(
                            buildRun.Id,
                            "当前没有可批准的工具策略（工具列表为空）",
                            ct).ConfigureAwait(false)
                        : await _buildRunCoordinator.ApprovePlanAsync(
                            buildRun.Id,
                            new ApprovedToolPolicy(approvedTools),
                            "runtime-approved",
                            ct).ConfigureAwait(false);
                yield return BuildRunStateEvent.From(buildRun);
            }

            if (buildRun.State == BuildRunState.Planned)
            {
                // No interaction channel available — fail closed instead of silently executing.
                buildRun = await _buildRunCoordinator.RejectPlanAsync(
                    buildRun.Id,
                    "无审批通道（_clarificationInteraction 不可用）",
                    ct).ConfigureAwait(false);
                yield return BuildRunStateEvent.From(buildRun);
            }

            if (buildRun.State == BuildRunState.Clarifying
                || BuildStateTransitionService.IsTerminal(buildRun.State))
            {
                if (buildRun.State == BuildRunState.Completed)
                    yield return new BuildRunCompletedEvent(CreateBuildRunResult(buildRun, buildRun.FailureSummary));
                yield return new DoneEvent(
                    null,
                    null,
                    0,
                    ResolveTerminalReason(buildRun),
                    conversationId);
                yield break;
            }
        }

        var previousBuildRunId = OneCodeAgentRunContext.CurrentBuildRunId;
        OneCodeAgentRunContext.CurrentBuildRunId = buildRun?.Id.ToString();
        var useDurableBuildAttempt = buildRun is not null;
        if (useDurableBuildAttempt
            && (_controlledBuildAttemptHost is null || _buildRunCoordinator is null || _buildRunStore is null))
        {
            throw new InvalidOperationException(
                "Controlled Build requires the durable attempt host, coordinator and BuildRun store.");
        }
        var channel = System.Threading.Channels.Channel.CreateUnbounded<object>();

        try
        {
            var options = new MainAgentRunOptions
            {
                ModelId = modelId,
                SystemPrompt = systemPrompt,
                UserPrompt = userPrompt,
                UserMessage = isMultimodal ? lastUserMessage : null,
                Messages = historyMessages,
                WorkingDirectory = workingDirectory,
                // 优先从 IConfigManager.Current.Effective.MaxTurns 动态读取（支持运行时 /config 修改），
                // 回退到构造函数参数。
                MaxTurns = ResolveMaxTurns(),
                EnableThinking = thinkingBudget > 0,
                ThinkingBudgetTokens = thinkingBudget ?? 0,
                Tools = localTools.Cast<AITool>().ToList(),
                ToolCapabilities = ToolActivationContext.CurrentCapabilities,
                WorkingMode = workingMode,
                FileChangeCallback = fileChangeCallback,
                ConversationId = conversationId,
                AgentRunId = agentRunId,
                // 从 IConfigManager.Current.Effective.MaxBudgetUsd 动态读取。
                // AppSettings.MaxBudgetUsd 是 double，MainAgentRunOptions.MaxBudgetUsd 是 decimal?，需转换。
                MaxBudgetUsd = ResolveMaxBudgetUsd(),
            };

            var textBuilder = new StringBuilder();
            var turnCount = 0;
            var totalInputTokens = 0;
            var totalOutputTokens = 0;
            var totalCacheReadTokens = 0;
            var totalCacheWriteTokens = 0;
            var turnStarted = false;
            var pendingTurnBoundary = false; // true after seeing tool results  — next text starts a new turn
            var toolNamesByCallId = new Dictionary<string, string>(StringComparer.Ordinal);
            // MAF may replay the same FunctionCallContent/FunctionResultContent across AgentResponseUpdate
            // boundaries (turn history replay). Without CallId-based dedup the same ToolStart/ToolDone event
            // is yielded twice, producing duplicated tool rows in the message list.
            var emittedToolCallIds = new HashSet<string>(StringComparer.Ordinal);
            var toolBatchCollector = new ToolBatchCollector(agentRunId);
            var nextPromptParser = includeNextPrompt ? new NextPromptTagStreamParser() : null;

            var runTask = useDurableBuildAttempt
                ? RunControlledBuildAttemptAsync(
                    buildRun!,
                    options,
                    localTools,
                    channel.Writer,
                    ct)
                : _mainAgentRunner.RunStreamingAsync(options, channel.Writer, ct);

            try
            {
                await foreach (var evt in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                {
                    // 事件驱动审批：ApprovalRequestEvent 直接透传给 TUI 消费
                    if (evt is ApprovalRequestEvent approvalEvt)
                    {
                        yield return approvalEvt;
                        continue;
                    }

                    if (evt is BuildRunStateEvent buildStateEvent)
                    {
                        yield return buildStateEvent;
                        continue;
                    }

                    if (evt is AgentResponseUpdate update)
                    {
                        if (TryExtractUsage(update, out var usage))
                        {
                            totalInputTokens = usage.InputTokens;
                            totalOutputTokens = usage.OutputTokens;
                            totalCacheReadTokens = usage.CacheReadTokens;
                            totalCacheWriteTokens = usage.CacheWriteTokens;
                            yield return new UsageUpdateEvent(usage);
                        }

                        // 检测工具调用、工具结果、推理内容，发射对应事件
                        if (update.Contents is { Count: > 0 })
                        {
                            foreach (var c in update.Contents)
                            {
                                if (c is FunctionCallContent fcc)
                                {
                                    var toolName = fcc.Name ?? "(unknown)";
                                    if (fcc.CallId is not null)
                                        toolNamesByCallId[fcc.CallId] = toolName;

                                    // 链路三：未知工具兜底——模型 hallucinate 的工具名若在注册表中存在但未加载，
                                    // 自动激活它，使下一轮工具列表包含该工具。
                                    TryAutoActivateUnknownTool(toolName, localTools);

                                    // Skip duplicates replayed by MAF across turn-boundary updates
                                    if (fcc.CallId is null || !emittedToolCallIds.Add("start:" + fcc.CallId))
                                        continue;
                                    var toolInput = ExtractToolInputSummary(fcc);
                                    // Collect for persistence so /files can extract paths.
                                    // Serialization failure must not forge "{}" — skip persistence of that block.
                                    var serializedArgs = SerializeArguments(fcc.Arguments);
                                    if (serializedArgs is not null)
                                        toolBatchCollector.AddCall(fcc, serializedArgs);
                                    yield return new ToolStartEvent(
                                        fcc.CallId ?? "",
                                        toolName,
                                        toolInput);
                                }
                                else if (c is FunctionResultContent frc)
                                {
                                    // Skip duplicates replayed by MAF across turn-boundary updates
                                    if (frc.CallId is null || !emittedToolCallIds.Add("done:" + frc.CallId))
                                        continue;
                                    var (isError, resultText) = ExtractToolResult(frc);
                                    var name = (frc.CallId is not null
                                        && toolNamesByCallId.TryGetValue(frc.CallId, out var n)) ? n : "(unknown)";
                                    toolBatchCollector.AddResult(
                                        frc,
                                        name,
                                        resultText ?? "null",
                                        isError,
                                        isError ? ToolResultCompletion.Failed : ToolResultCompletion.Succeeded);
                                    yield return new ToolDoneEvent(
                                        frc.CallId ?? "",
                                        name,
                                        isError,
                                        resultText);
                                    pendingTurnBoundary = true;
                                }
                                else if (c is TextReasoningContent trc && !string.IsNullOrEmpty(trc.Text))
                                {
                                    yield return new ThinkingDeltaEvent(trc.Text);
                                }
                            }
                        }

                        // Detect turn boundaries: new assistant text after tool results = new turn
                        if (pendingTurnBoundary && !string.IsNullOrEmpty(update.Text))
                        {
                            pendingTurnBoundary = false;
                            turnStarted = false; // force new turn on next text
                        }

                        if (!string.IsNullOrEmpty(update.Text))
                        {
                            var segments = nextPromptParser is null
                                ? [(Text: update.Text, Suggestion: (string?)null)]
                                : nextPromptParser.Process(update.Text);

                            foreach (var (text, suggestion) in segments)
                            {
                                if (!string.IsNullOrEmpty(text))
                                {
                                    textBuilder.Append(text);
                                    if (!turnStarted)
                                    {
                                        turnCount++;
                                        turnStarted = true;
                                        yield return new TurnStartedEvent(turnCount);
                                    }
                                    yield return new TextDeltaEvent(text);
                                }

                                if (!string.IsNullOrEmpty(suggestion))
                                    yield return new SuggestionsEvent([suggestion]);
                            }
                        }
                    }
                }
            }
            finally
            {
                if (ct.IsCancellationRequested)
                {
                    await PersistCancelledRunAsync(
                        conversationId,
                        agentRunId,
                        workingMode,
                        toolBatchCollector,
                        CancellationToken.None).ConfigureAwait(false);
                }
                OneCodeAgentRunContext.CurrentBuildRunId = previousBuildRunId;
            }

            if (nextPromptParser?.Flush() is { Length: > 0 } remainingText)
            {
                textBuilder.Append(remainingText);
                if (!turnStarted)
                {
                    turnCount++;
                    turnStarted = true;
                    yield return new TurnStartedEvent(turnCount);
                }
                yield return new TextDeltaEvent(remainingText);
            }

            if (turnStarted)
            {
                yield return new TurnCompletedEvent(turnCount, false);
            }

            // Catch non-cancellation failures and fire StopFailure hook
            Exception? runException = null;
            MainAgentRunResult? runResult = null;
            try
            {
                runResult = await runTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Agent run failed");
                runException = ex;
            }

            if (runException is not null)
            {
                await FireHookAsync(HookEvent.StopFailure, sessionId, workingDirectory, ct);
                await NotifyAsync("OneCode 任务执行失败", runException.Message, ct).ConfigureAwait(false);
                yield return new ErrorEvent(runException.Message);
                yield break;
            }

            var finalText = textBuilder.ToString();
            var finalUsage = new TokenUsage(
                totalInputTokens,
                totalOutputTokens,
                CacheReadTokens: totalCacheReadTokens,
                CacheWriteTokens: totalCacheWriteTokens);

            try
            {
                if (conversationId is { } completedConversationId)
                {
                    if (toolBatchCollector.CompletedBatches.Count > 0)
                    {
                        await _sessionManager.AppendCompletedToolBatchesAsync(
                                completedConversationId,
                                toolBatchCollector.CompletedBatches,
                                ct)
                            .ConfigureAwait(false);
                    }

                    if (toolBatchCollector.HasOpenBatch)
                    {
                        _logger.LogWarning(
                            "Dropping incomplete tool batch for conversation {SessionId}, run {RunId}",
                            completedConversationId,
                            agentRunId);
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
                _logger.LogWarning(ex, "Failed to persist assistant transcript for session {SessionId}", sessionId);
            }

            await CompletePlanRunIfPendingAsync(
                conversationId,
                agentRunId,
                workingMode,
                toolBatchCollector.HasOpenBatch,
                ct).ConfigureAwait(false);

            // Fire Stop hook before completing
            await FireHookAsync(HookEvent.Stop, sessionId, workingDirectory, ct);
            await NotifyAsync("OneCode 任务执行完成", finalText.Length > 200 ? finalText[..200] + "…" : finalText, ct).ConfigureAwait(false);

            // 记录 token 使用量和分场景估算到 TokenUsageTracker
            var toolsForBreakdown = localTools.ToList();
            var messagesForBreakdown = historyMessages ?? Array.Empty<ChatMessage>();
            var breakdown = _tokenBreakdownEstimator.Estimate(
                systemPrompt, toolsForBreakdown, messagesForBreakdown, totalInputTokens);
            _tokenUsageTracker.Record(finalUsage, breakdown);

            // Compute real terminal reason: combine runner result with turn-limit detection.
            var terminalReason = runResult?.TerminalReason ?? BuildTerminalReason.Completed;
            var transactionRolledBack = runResult?.TransactionRolledBack ?? false;
            var validationFailureSummary = runResult?.ValidationFailureSummary;

            // If the agent didn't explicitly signal a terminal reason, check turn limit.
            if (terminalReason == BuildTerminalReason.Completed && turnCount >= options.MaxTurns)
            {
                terminalReason = BuildTerminalReason.TurnLimitReached;
            }

            // Detect budget exceeded from final text (BudgetGuard middleware short-circuits with a text marker).
            if (terminalReason == BuildTerminalReason.Completed
                && finalText.Contains("[Budget Exceeded]", StringComparison.OrdinalIgnoreCase))
            {
                terminalReason = BuildTerminalReason.BudgetExceeded;
            }

            if (useDurableBuildAttempt && buildRun is not null && _buildRunStore is not null)
            {
                buildRun = await _buildRunStore.LoadByIdAsync(buildRun.Id, ct).ConfigureAwait(false)
                    ?? throw new InvalidOperationException($"BuildRun '{buildRun.Id}' disappeared after its durable attempt.");
                terminalReason = ResolveTerminalReason(buildRun);
                transactionRolledBack = buildRun.TransactionRolledBack;
                validationFailureSummary = buildRun.FailureSummary;
                if (buildRun.State == BuildRunState.Completed)
                    yield return new BuildRunCompletedEvent(CreateBuildRunResult(buildRun, finalText));
            }

            yield return new DoneEvent(
                finalText,
                finalUsage,
                turnCount,
                terminalReason,
                conversationId,
                transactionRolledBack,
                validationFailureSummary);

            UpdateCacheSafeParams(systemPrompt, modelId, thinkingBudget, workingDirectory, localTools);
        }
        finally
        {
            OneCodeAgentRunContext.CurrentBuildRunId = previousBuildRunId;
        }
    }

    private async Task<MainAgentRunResult> RunControlledBuildAttemptAsync(
        BuildRun buildRun,
        MainAgentRunOptions options,
        IReadOnlyList<AIFunction> localTools,
        System.Threading.Channels.ChannelWriter<object> eventWriter,
        CancellationToken ct)
    {
        var host = _controlledBuildAttemptHost
            ?? throw new InvalidOperationException("Controlled Build attempt host is not configured.");
        var coordinator = _buildRunCoordinator
            ?? throw new InvalidOperationException("BuildRun coordinator is not configured.");
        var store = _buildRunStore
            ?? throw new InvalidOperationException("BuildRun store is not configured.");
        var runtime = new ControlledBuildAttemptRuntime(
            _mainAgentRunner,
            coordinator,
            store,
            new ControlledBuildAttemptContext(
                options,
                eventWriter,
                static () => new EditTransaction(),
                static run => BuildRunStateEvent.From(run),
                Ledger: _operationLedger));
        var toolCapabilityHash = ControlledBuildAttemptWorkflowCompiler.ComputeToolCapabilityHash(
            ControlledBuildAttemptWorkflowCompiler.ApprovedPolicyCapabilities(buildRun, options.ToolCapabilities),
            buildRun.ApprovedToolPolicy?.ToolNames ?? []);

        try
        {
            var result = await host.RunNextAsync(
                buildRun,
                options.ModelId,
                options.SystemPrompt,
                toolCapabilityHash,
                runtime,
                new System.Text.Json.JsonSerializerOptions(),
                ct: ct).ConfigureAwait(false);
            return result.Output.Result;
        }
        finally
        {
            eventWriter.TryComplete();
        }
    }

    /// <summary>
    /// Snapshots the tool capabilities for the current working mode as the plan-approval tool policy.
    /// Resolved explicitly (not via ToolActivationContext.AsyncLocal) because the plan-approval gate runs
    /// inside an async iterator where ExecutionContext propagation across yields is not reliable.
    /// </summary>
    private IReadOnlyList<string> SnapshotApprovedTools()
        => _toolCapabilityResolver.Resolve(WorkingMode.Build).AllowedToolNames
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static string BuildPlanApprovalPrompt(
        BuildRun buildRun,
        IReadOnlyList<string> approvedTools)
    {
        var planSummary = buildRun.Plan?.Summary ?? "（无计划摘要）";
        var toolSummary = approvedTools.Count == 0
            ? "（空）"
            : string.Join(", ", approvedTools);
        return $"{planSummary}\n\n本次执行允许工具：{toolSummary}";
    }

    private static BuildTerminalReason ResolveTerminalReason(BuildRun run)
        => run.State == BuildRunState.Clarifying
            ? BuildTerminalReason.ClarificationRequired
            : run.TerminalReason ?? run.State switch
            {
                BuildRunState.Completed => BuildTerminalReason.Completed,
                BuildRunState.Blocked => BuildTerminalReason.Blocked,
                BuildRunState.Cancelled => BuildTerminalReason.Cancelled,
                BuildRunState.LimitReached => BuildTerminalReason.TurnLimitReached,
                BuildRunState.BudgetExceeded => BuildTerminalReason.BudgetExceeded,
                _ => BuildTerminalReason.AgentException,
            };

    private static BuildRunResult CreateBuildRunResult(BuildRun run, string? summary) =>
        new(
            run.Id,
            run.State,
            run.TerminalReason ?? BuildTerminalReason.AgentException,
            summary,
            run.ChangedFiles,
            run.Plan?.Tasks ?? [],
            run.Validations,
            run.Scope?.AcceptanceCriteria ?? [],
            run.Plan?.Risks ?? [],
            run.DeliveryManifest,
            run.TransactionCommitted,
            run.TransactionRolledBack,
            run.Metrics);

    private async Task PersistCancelledRunAsync(
        SessionId? conversationId,
        string agentRunId,
        WorkingMode workingMode,
        ToolBatchCollector collector,
        CancellationToken ct)
    {
        if (conversationId is not { } sessionId)
            return;

        if (collector.CompletedBatches.Count > 0)
        {
            await _sessionManager.AppendCompletedToolBatchesAsync(
                sessionId,
                collector.CompletedBatches,
                ct).ConfigureAwait(false);
        }

        if (_planWorkflow is null || workingMode != WorkingMode.Plan)
            return;
        var workflow = await _planWorkflow.GetAsync(sessionId, ct).ConfigureAwait(false);
        if (workflow is null
            || workflow.State is PlanWorkflowState.Completed or PlanWorkflowState.Failed or PlanWorkflowState.Cancelled)
        {
            return;
        }
        if (workflow.ActiveRunId is not null
            && !string.Equals(workflow.ActiveRunId, agentRunId, StringComparison.Ordinal))
        {
            return;
        }

        await _planWorkflow.CancelAsync(new CancelPlanCommand(
            $"cancel-run-{agentRunId}",
            sessionId,
            workflow.Id,
            workflow.Version,
            "Plan agent run was cancelled."), ct).ConfigureAwait(false);
    }

    private async Task CompletePlanRunIfPendingAsync(
        SessionId? conversationId,
        string agentRunId,
        WorkingMode workingMode,
        bool hasOpenToolBatch,
        CancellationToken ct)
    {
        if (_planWorkflow is null
            || conversationId is not { } sessionId
            || workingMode != WorkingMode.Plan)
        {
            return;
        }

        var workflow = await _planWorkflow.GetAsync(sessionId, ct).ConfigureAwait(false);
        if (workflow is null
            || workflow.State != PlanWorkflowState.FinalizingPlanRun
            || !string.Equals(workflow.ActiveRunId, agentRunId, StringComparison.Ordinal))
        {
            return;
        }

        var protocolValid = !hasOpenToolBatch
            && _toolProtocolValidator.Validate(_sessionManager.GetChatHistory(sessionId)).IsValid;
        await _planWorkflow.HandleRunEventAsync(
            new PlanRunCompletedEvent(
                sessionId,
                workflow.Id,
                agentRunId,
                protocolValid,
                DateTimeOffset.UtcNow),
            ct).ConfigureAwait(false);

        if (protocolValid && _planCardPublisher is not null)
        {
            var awaiting = await _planWorkflow.GetAsync(sessionId, ct).ConfigureAwait(false);
            if (awaiting?.State == PlanWorkflowState.AwaitingApproval
                && awaiting.SubmittedRevision is { } revision)
            {
                _planCardPublisher.Publish(awaiting);
            }
        }
    }

    /// <summary>
    /// 链路三：未知工具兜底。本地小模型经常凭名字 hallucinate 调用工具，
    /// 当前行为是直接报 "unknown tool"。改为——若该名字在注册表中存在且未加载，
    /// 自动激活并返回提示。这把最高频的失败模式变成了自愈路径。
    /// </summary>
    private void TryAutoActivateUnknownTool(string toolName, IReadOnlyList<AIFunction> localTools)
    {
        // 只处理在注册表中存在、但不在当前工具列表中的工具
        var meta = _toolCatalog.Metadata.Get(toolName);
        if (meta is null || !meta.IsVisible || !meta.IsEnabled)
            return;

        // 如果工具已在当前列表中，无需激活
        if (localTools.Any(t => t.Name.Equals(toolName, StringComparison.OrdinalIgnoreCase)))
            return;

        if (_sessionToolSetManager.TryActivate(toolName))
        {
            _logger.LogInformation(
                "Auto-activated tool '{ToolName}' via unknown-tool fallback (Chain 3). " +
                "It will be available in the next turn.",
                toolName);
        }
    }

    /// <summary>
    /// 从 <see cref="FunctionCallContent"/> 中提取工具参数摘要，
    /// 用于 TUI 在工具开始阶段展示操作目标（如文件路径、命令）。
    /// </summary>
    private string? ExtractToolInputSummary(FunctionCallContent fcc)
    {
        try
        {
            if (fcc.Arguments is null) return null;

            // File path extraction delegates to ToolArgumentExtractor (handles filePath/file_path/path keys).
            var filePath = OneCode.Core.Tools.ToolArgumentExtractor.ExtractFilePath(fcc.Arguments);
            if (filePath is { Length: > 0 }) return filePath;

            // Arguments 可能是 IDictionary<string, object?> 或 JsonElement
            if (TryGetArgument(fcc.Arguments, "command", out var cmd)) return Truncate(cmd, 80);
            if (TryGetArgument(fcc.Arguments, "pattern", out var pat)) return Truncate(pat, 80);
            if (TryGetArgument(fcc.Arguments, "query", out var q)) return Truncate(q, 80);
            if (string.Equals(fcc.Name, "AskUserQuestion", StringComparison.OrdinalIgnoreCase)
                && TryGetArgument(fcc.Arguments, "question", out var question))
                return Truncate(question, 80);
            if (string.Equals(fcc.Name, "AskUserQuestions", StringComparison.OrdinalIgnoreCase)
                && TryGetArgument(fcc.Arguments, "title", out var title))
                return Truncate(title, 80);

            // Fallback: JSON 摘要（使用不转义 Unicode 的选项）
            var json = System.Text.Json.JsonSerializer.Serialize(fcc.Arguments, new System.Text.Json.JsonSerializerOptions
            {
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                WriteIndented = false
            });
            return Truncate(json, 100);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "ExtractToolInputSummary failed for tool {Tool}", fcc.Name);
            return null;
        }
    }

    /// <summary>
    /// Serializes FunctionCallContent.Arguments to a JSON string for persistence
    /// in ToolUseBlock. Handles IDictionary and JsonElement inputs.
    /// Returns null on failure — callers must not treat failure as an empty object.
    /// </summary>
    private string? SerializeArguments(object? arguments)
    {
        if (arguments is null) return "{}";
        try
        {
            return System.Text.Json.JsonSerializer.Serialize(arguments, new System.Text.Json.JsonSerializerOptions
            {
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                WriteIndented = false,
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to serialize tool arguments; skipping ToolUseBlock persistence");
            return null;
        }
    }

    private bool TryGetArgument(object arguments, string key, out string value)
    {
        value = "";
        try
        {
            if (arguments is System.Collections.IDictionary dict && dict.Contains(key))
            {
                value = dict[key]?.ToString() ?? "";
                return value.Length > 0;
            }
            if (arguments is JsonElement el && el.ValueKind == JsonValueKind.Object)
            {
                if (el.TryGetProperty(key, out var prop) && prop.ValueKind == JsonValueKind.String)
                {
                    value = prop.GetString() ?? "";
                    return value.Length > 0;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "TryGetArgument('{Key}') failed", key);
        }
        return false;
    }

    private static string Truncate(string s, int max) =>
        s.Length > max ? s[..(max - 3)] + "..." : s;

    /// <summary>
    /// 从 <see cref="FunctionResultContent"/> 中提取错误标记和结果文本。
    /// </summary>
    private (bool IsError, string? Result) ExtractToolResult(FunctionResultContent frc)
    {
        try
        {
            var result = frc.Result;
            if (result is Exception ex)
                return (true, ex.Message);
            if (result is string s)
                return (false, s);
            if (result is Core.Tools.ToolResult tr)
            {
                var text = Core.Tools.ToolResultSerializer.Serialize(tr);
                return (tr.IsError, text);
            }
            if (result is null)
                return (false, null);
            var json = System.Text.Json.JsonSerializer.Serialize(result, new System.Text.Json.JsonSerializerOptions
            {
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                WriteIndented = false
            });
            return (false, json.Length > 500 ? json[..497] + "..." : json);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "ExtractToolResult failed for call {CallId}", frc.CallId);
            return (false, null);
        }
    }

    /// <summary>
    /// Extracts token usage from streaming updates using the standard
    /// <see cref="UsageContent"/> API from Microsoft.Extensions.AI.
    /// 提取顺序：
    ///   1. 标准字段：InputTokenCount / OutputTokenCount / CachedInputTokenCount
    ///   2. 厂商特定字段：AdditionalCounts 字典（如 Anthropic cache_creation_input_tokens）
    /// </summary>
    private static bool TryExtractUsage(AgentResponseUpdate update, out TokenUsage usage)
    {
        usage = new TokenUsage(0, 0);

        if (update.Contents is not { Count: > 0 })
            return false;

        var usageContent = update.Contents.OfType<UsageContent>().FirstOrDefault();
        if (usageContent?.Details is not { } details)
            return false;

        var input = SafeInt(details.InputTokenCount);
        var output = SafeInt(details.OutputTokenCount);
        if (input == 0 && output == 0)
            return false;

        var cacheRead = SafeInt(details.CachedInputTokenCount);

        // Anthropic 缓存写 token 通过 AdditionalCounts 字典提供
        // 常见键名：cache_creation_input_tokens / cache_creation / cacheWriteInputTokens
        var cacheWrite = ExtractAdditionalCount(details,
            "cache_creation_input_tokens",
            "cache_creation",
            "cacheWriteInputTokens",
            "cache_write_input_tokens");

        usage = new TokenUsage(
            input,
            output,
            CacheReadTokens: cacheRead,
            CacheWriteTokens: cacheWrite);
        return true;
    }

    /// <summary>
    /// 从 UsageDetails.AdditionalCounts 字典中提取厂商特定的 token 计数。
    /// AdditionalCounts 类型为 IReadOnlyDictionary&lt;string, long&gt;。
    /// </summary>
    private static int ExtractAdditionalCount(UsageDetails details, params string[] keys)
    {
        if (details.AdditionalCounts is null || keys.Length == 0)
            return 0;

        foreach (var key in keys)
        {
            if (details.AdditionalCounts.TryGetValue(key, out var value))
            {
                return SafeInt(value);
            }
        }

        return 0;
    }

    private static int SafeInt(long? value)
        => value is null or 0 ? 0 : value > int.MaxValue ? int.MaxValue : (int)value.Value;

    /// <summary>
    /// Builds the active tool list. For Ollama, uses session-level tool activation:
    /// Always tools + session-activated tools (monotonic growth, prompt-stable ordering).
    /// Cloud models receive the full catalog.
    /// </summary>
    /// <remarks>
    /// 「三条激活链路」设计说明见 <see cref="SessionToolSet"/> 的类级文档（单一权威来源）。
    /// </remarks>
    private IReadOnlyList<AIFunction> AssembleTools(
        string userPrompt,
        ToolCapabilitySet capabilities,
        SessionId? conversationId = null)
    {
        var allTools = _toolCatalog.Tools
            .Where(tool => capabilities.AllowedToolNames.Contains(tool.Name))
            .ToList();
        var provider = _configManager.Current.Effective.Provider?.ToLowerInvariant();

        // P3: 使用 ModelCapabilities.RequiresToolFiltering 替代 provider == "ollama" 一刀切
        // 云端模型（Anthropic/OpenAI 等）始终全量；本地模型按上下文窗口决定：
        // ≥ 32K 走全量（prompt caching 更高效），< 32K 走过滤（SessionToolSet 分层加载）
        var contextWindow = _configManager.Current.Effective.OllamaContextWindow;
        var needsFiltering = ModelCapabilities.RequiresToolFiltering(provider, contextWindow);

        if (!needsFiltering)
        {
            _logger.LogDebug("Assembled {Total} tools (full catalog for provider={Provider}, contextWindow={ContextWindow})",
                allTools.Count, provider ?? "default", contextWindow);
            return allTools;
        }

        // Filtered path: session-level tool activation via SessionToolSet
        if (conversationId is { } convId)
        {
            var session = _sessionToolSetManager.GetOrCreate(convId.ToString());
            var selected = session.GetTools(userPrompt, capabilities);

            _logger.LogDebug("Filtered session tool selection: {Selected}/{Total} tools (activated: {Activated})",
                selected.Count, allTools.Count, session.ActivatedNames.Count);

            return selected;
        }

        // No session — return full catalog (e.g. UpdateCacheSafeParams before first query)
        _logger.LogDebug("Assembled {Total} tools (no session, full catalog)", allTools.Count);
        return allTools;
    }

    /// <summary>
    /// 从 <see cref="AppSettings.MaxTurns"/> 动态解析最大轮数。
    /// 支持运行时通过 /config 命令修改 maxTurns 后立即生效。
    /// </summary>
    private int ResolveMaxTurns() =>
        _configManager.Current.Effective.MaxTurns;

    /// <summary>
    /// 从 <see cref="AppSettings.MaxBudgetUsd"/> 动态解析预算上限。
    /// 支持运行时通过 /config 命令修改 maxBudgetUsd 后立即生效。
    /// </summary>
    /// <remarks>
    /// <see cref="AppSettings.MaxBudgetUsd"/> 是 <c>double</c>，
    /// <see cref="MainAgentRunOptions.MaxBudgetUsd"/> 是 <c>decimal?</c>，需显式转换。
    /// </remarks>
    private decimal? ResolveMaxBudgetUsd() =>
        (decimal?)_configManager.Current.Effective.MaxBudgetUsd;

    private void UpdateCacheSafeParams(string systemPrompt, string modelId, int? thinkingBudget, string? workingDirectory, IReadOnlyList<AIFunction> localTools)
    {
        // 使用 localTools 的冻结快照——子代理通过 CacheSafeParams.Tools 获取工具列表，
        // 必须是独立副本而非共享可变引用（SessionToolSet 在后续轮次可能继续追加工具）。
        var toolList = localTools.Cast<AITool>().ToList();

        _lastCacheSafeParams = new CacheSafeParams
        {
            SystemPrompt = systemPrompt,
            ModelId = modelId,
            ThinkingBudget = thinkingBudget,
            Tools = toolList.Count > 0 ? toolList : null,
            ToolCapabilities = ToolActivationContext.CurrentCapabilities,
            Metadata = new Dictionary<string, object?> { ["turn"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds() }
        };
    }

    private async Task FireHookAsync(HookEvent @event, SessionId? sessionId, string? workingDirectory,
        CancellationToken ct = default)
    {
        var payload = new HookPayload
        {
            Event = @event,
            SessionId = sessionId?.ToString(),
            Cwd = workingDirectory ?? Environment.CurrentDirectory,
        };

        await _hookExecutionService.FireAsync(payload, ct: ct);
    }

    private async Task NotifyAsync(string title, string message, CancellationToken ct)
    {
        // 配置开关：默认关闭，用户需显式开启 notificationsEnabled=true 才发桌面通知
        if (!_configManager.Current.Effective.NotificationsEnabled) return;
        if (!_notifierService.IsSupported) return;
        await _notifierService.SendNotificationAsync(title, message, ct).ConfigureAwait(false);
    }

    private static IReadOnlyList<ChatMessage> BuildHistoryWithoutLatestUser(
        IReadOnlyList<ChatMessage> history,
        string latestUserPrompt)
    {
        if (history.Count == 0)
            return history;

        if (history[^1].Role == ChatRole.User
            && string.Equals(history[^1].Text, latestUserPrompt, StringComparison.Ordinal))
        {
            return history.Count == 1
                ? []
                : history.Take(history.Count - 1).ToList();
        }

        return history;
    }
}

