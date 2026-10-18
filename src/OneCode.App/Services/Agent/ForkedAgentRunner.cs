using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OneCode.App.Query;
using OneCode.Core.Config;
using OneCode.Core.Coordinator;
using OneCode.Core.Errors;
using OneCode.Core.Models;
using OneCode.Infrastructure.Agent;
using CoreConstants = OneCode.Core.Constants;

namespace OneCode.App.Services.Agent;

/// <summary>
/// Product sub-agent runner (<see cref="IAgentRunner"/>) for AgentTool / ParallelAgents / DAG / Worker.
/// Builds a child HarnessAgent via <see cref="SubAgentPipelineFactory"/> with OneCode
/// <see cref="PipelineProfile"/> (tool allowlists, Explore/Plan read-only, TaskService via Worker).
/// </summary>
/// <remarks>
/// 与 MAF <c>BackgroundAgentsProvider</c> 的边界已固化为决策记录
/// [子代理派工边界](../../../docs/adr/0011-background-agents-delegation-boundary.md)：
/// BackgroundAgents 是另一个控制面——给父 Agent 注入模型侧工具（StartTask/Wait/GetResults），
/// 不起子代理的决定权归父 LLM。它**不**替代本 runner，也不用作其底层实现
/// （provider 无编程式起任务入口，公共面只有 GetIncompleteTasks / ReleaseSessionAsync）。
/// 禁止在默认 Full 路径挂 BackgroundAgents（派工双挂 + 产品闸丢失 + 审批不转发）。
/// </remarks>
public sealed class ForkedAgentRunner(
    ILogger<ForkedAgentRunner> logger,
    ILoggerFactory loggerFactory,
    IServiceProvider serviceProvider,
    AgentContextPipeline contextPipeline,
    SubAgentPipelineFactory pipelineFactory,
    WorkingModeController modeController,
    ForkedAgentRuntimeDependencies runtime,
    PromptComposer promptComposer) : IAgentRunner
{
    private readonly ILogger<ForkedAgentRunner> _logger = logger;
    private readonly ILoggerFactory _loggerFactory = loggerFactory;
    private readonly IChatClient _chatClient = runtime.ChatClient;
    // IServiceProvider 仅用于传递给 MAF 的 ChatClientAgentBuildOptions.ServiceProvider
    // （MAF 框架要求），不得用于业务逻辑中的 GetService<T>() 调用。
    private readonly IServiceProvider _serviceProvider = serviceProvider;
    private readonly AgentContextPipeline _contextPipeline = contextPipeline;
    private readonly SubAgentPipelineFactory _pipelineFactory = pipelineFactory;
    private readonly IModelManager _modelManager = runtime.ModelManager;
    private readonly IWorkingDirectoryAccessor _workingDirectoryAccessor = runtime.WorkingDirectory;
    private readonly ISessionIdProvider _sessionIds = runtime.SessionIds;
    private readonly Core.Tools.ToolMetadataRegistry _toolMetadata = runtime.ToolMetadata;
    private readonly CompactionStrategyFactory _compactionBuilder = runtime.CompactionBuilder;
    private readonly IConfigManager _configManager = runtime.ConfigManager;
    private readonly PromptComposer _promptComposer = promptComposer;
    private readonly WorkingModeController _modeController = modeController;
    private readonly ConcurrentDictionary<string, ForkedAgentRun> _activeRuns = new();

    // 工具调用安全上限 = 轮数上限 × 该系数：并行调用一轮可发多个调用，逐调用计数比轮数
    // 消耗更快。上限只在"达到轮预算之前先触发收尾提示"的意义上存在——触发后中间件返回
    // 可指导收尾的错误消息，模型用预留的收尾迭代产出总结。
    private const int ToolCallHeadroomFactor = 4;

    /// <summary>
    /// 分层解析子代理轮数上限：frontmatter（MaxTurnsCeiling）> settings（agents.defaults.maxTurns，
    /// ≤0 视为未配置）> profile 内置默认（只读 15 / 通用 50）。调用方请求的 MaxTurns 只能收窄。
    /// </summary>
    internal static int ResolveMaxTurns(PipelineProfile profile, int? maxTurnsCeiling, int configuredMaxTurns)
    {
        var ceiling = maxTurnsCeiling ?? (configuredMaxTurns > 0
            ? configuredMaxTurns
            : DefaultMaxTurnsFor(profile));
        return Math.Max(1, ceiling);
    }

    private static int DefaultMaxTurnsFor(PipelineProfile profile) =>
        profile is PipelineProfile.Explore or PipelineProfile.Plan
            ? CoreConstants.Session.SubAgentMaxTurnsReadOnlyDefault
            : CoreConstants.Session.SubAgentMaxTurnsDefault;

    public async Task<ForkedAgentResult> RunForkedAgentAsync(
        ForkedAgentParams parameters,
        CancellationToken ct = default)
    {
        var runId = Guid.NewGuid().ToString("N")[..8];
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        // 墙钟超时走 linked CTS：到时取消与外部取消在 catch 中按 ct.IsCancellationRequested 区分。
        if (parameters.Timeout is { } timeout)
            cts.CancelAfter(timeout);
        var run = new ForkedAgentRun
        {
            Id = runId,
            Label = parameters.ForkLabel ?? "unnamed",
            StartedAt = DateTimeOffset.UtcNow,
            CancellationTokenSource = cts,
        };

        _activeRuns[runId] = run;

        // 子代理进度旁路：读取父 run 注入的事件写入器（AsyncLocal，StartRun 同段注入——
        // 与 CurrentBuildRunId 同一模式；受控 Build attempt 在 ControlledBuildAttemptRuntime
        // 内同段重建）。null = 非流式主查询上下文（GOAL DAG / 测试宿主），静默跳过上报；
        // 父 run 收尾后 TryWrite 是安全 no-op。
        var progressWriter = OneCodeAgentRunContext.SubagentEventWriter;
        var startedAt = run.StartedAt;
        var toolCalls = 0;
        AgentPipelineHandle? pipelineRef = null;

        // 轮数 = 真实模型往返数（管道指标）；管道装配前为 0。
        int CurrentTurns() => pipelineRef?.Metrics.ModelCallCount ?? 0;

        void ReportProgress(SubagentProgressPhase phase, string? currentTool = null, int turns = 0)
        {
            progressWriter?.TryWrite(new SubagentProgressUpdate(
                runId, parameters.ForkLabel ?? "sub-agent", phase,
                toolCalls, turns, currentTool,
                (long)(DateTimeOffset.UtcNow - startedAt).TotalMilliseconds));
        }

        ReportProgress(SubagentProgressPhase.Started);

        try
        {
            _logger.LogDebug("Starting forked agent '{Label}' (id={RunId})", run.Label, runId);

            using var transaction = new EditTransaction(
                _loggerFactory.CreateLogger<EditTransaction>());

            var csp = parameters.CacheSafeParams;
            var maxTurns = parameters.MaxTurns ?? CoreConstants.Session.SubAgentMaxTurnsDefault;
            var tools = FilterChildTools(csp?.Tools, parameters.Capabilities);
            // cwd 决策：显式覆盖（GOAL 隔离 worktree 等）优先，否则会话工作目录访问器。
            // ParentWorkingDirectory 此前声明但从未被消费，导致 GOAL worktree 中派生的
            // 子代理落在会话根目录——现在生效。
            var cwd = parameters.ParentWorkingDirectory ?? _workingDirectoryAccessor.WorkingDirectory;
            using var cwdScope = OneCodeAgentRunContext.UseWorkingDirectory(cwd);
            var profile = parameters.Profile;

            var contextProviders = _contextPipeline.BuildShared(
                profile,
                BuildContextProviderOptions(parameters, cwd));

            // MAF 不释放交给它的 AIContextProviders，而 skills 等 provider 是每 run 新建的。
            // fork 不经过主代理管道的租约，必须在这里自行持有并释放（含取消、异常与超时路径）。
            using var providerLease = AgentContextProviderLease.Track(contextProviders);

            _logger.LogInformation(
                "Forked agent '{Label}' (profile={Profile}) armed with {Count} tools, cwd={Cwd}",
                parameters.ForkLabel, profile, tools.Count, cwd);

            var providerId = csp?.ModelId is null ? null : _modelManager.Resolve(csp.ModelId)?.ProviderId;
            var linkedToken = cts.Token;

            var pipelineOptions = _pipelineFactory.BuildOptions(new SubAgentPipelineRequest
            {
                Profile = profile,
                WorkingDirectory = cwd,
                // 父级工作模式随请求透传：BUILD 下工厂按 fork 启动时点对 ledger 快照
                // 作预算基线，避免长 Plan 阶段的会话累计把子代理"开跑即触预算硬停"。
                ParentWorkingMode = _modeController.Mode,
                EditTransaction = transaction,
                // 工具调用安全上限 = 轮数 × 冗余系数（并行调用一轮可发多个调用）；
                // 轮数上限（模型往返次数）才是预算主口径。
                MaxToolCalls = maxTurns * ToolCallHeadroomFactor,
                MaxTurns = maxTurns,
                ModelId = csp?.ModelId,
                ProviderId = providerId,
                // usage 归属当前会话（UsageTrackingRunMiddleware 据此写会话桶，/status 才能看到
                // 子代理消耗）；上下文提供器仍取 parameters.ConversationId（fork 本地，
                // 派工路径均为 null），子代理不得读/写主会话的转录与设计上下文。
                ConversationId = _sessionIds.CurrentSessionId,
                AllowedTools = parameters.AllowedTools,
                // sink 常挂（progressWriter 为 null 时只计数不上报）。
                // 同一轮的并行工具调用可能并发触发事件，计数必须原子。
                OrchestrationEventSink = evt =>
                {
                    if (evt is OrchestrationEvent.ToolStart ts)
                    {
                        Interlocked.Increment(ref toolCalls);
                        ReportProgress(SubagentProgressPhase.Running, ts.Name, CurrentTurns());
                    }
                    else if (evt is OrchestrationEvent.ToolDone)
                    {
                        ReportProgress(SubagentProgressPhase.Running, turns: CurrentTurns());
                    }
                },
            });

            var pipeline = pipelineRef = AgentPipelineBuilder.BuildHarnessAgent(new ChatClientAgentBuildOptions
            {
                ChatClient = _chatClient,
                Name = parameters.ForkLabel ?? "sub-agent",
                ChatOptions = new ChatOptions
                {
                    ModelId = csp?.ModelId,
                    MaxOutputTokens = parameters.MaxOutputTokens,
                    Tools = tools.Count > 0 ? tools : null,
                    ToolMode = tools.Count > 0 ? ChatToolMode.Auto : null,
                    Instructions = parameters.AgentInstructions,
                },
                LoggerFactory = _loggerFactory,
                ServiceProvider = _serviceProvider,
                ToolMetadata = _toolMetadata,
                CompactionStrategy = await _compactionBuilder.BuildForWorkerAsync(
                    csp?.ModelId,
                    parameters.MaxOutputTokens,
                    profile is PipelineProfile.Explore or PipelineProfile.Plan,
                    linkedToken).ConfigureAwait(false),
                // MAF 把 harness 片段拼在 Instructions（角色正文）前面。此前该片段在这里取到却被
                // 丢弃，子 Agent 静默退化成 MAF 的通用默认指令，拿不到产品的注入防护指引。
                HarnessInstructions = parameters.HarnessInstructions,
                AgentContextProviders = contextProviders,
                PipelineOptions = pipelineOptions,
            });

            List<ChatMessage> chatMessages = [];

            if (csp?.SystemPrompt is { Length: > 0 } sysPrompt)
                chatMessages.Add(new ChatMessage(ChatRole.System, sysPrompt));

            if (parameters.PromptMessages is not null)
                chatMessages.AddRange(parameters.PromptMessages);

            var session = await pipeline.Agent.CreateSessionAsync(linkedToken).ConfigureAwait(false);
            var response = await pipeline.Agent.RunAsync(chatMessages, session, new AgentRunOptions(), linkedToken)
                .ConfigureAwait(false);

            // 预算触发判定：模型往返用掉了预留的收尾迭代（> maxTurns），或工具安全上限被拒绝过。
            var maxToolCalls = maxTurns * ToolCallHeadroomFactor;
            var budgetExhausted = pipeline.Metrics.ModelCallCount > maxTurns
                || pipeline.Metrics.ToolCallCount > maxToolCalls;
            var finalText = response.Text;
            if (budgetExhausted && string.IsNullOrWhiteSpace(finalText))
            {
                finalText = await RunWrapUpAsync(pipeline.Agent, session, run.Label, linkedToken).ConfigureAwait(false);
            }

            // 非交互 fork 无法答复 MAF ToolApprovalRequestContent：检测到挂起审批时
            // fail-closed 返回错误，避免静默丢弃工具调用（Worker 继承 Default 等模式会触发 Ask）。
            var pendingApprovals = response.Messages
                .SelectMany(message => message.Contents)
                .OfType<ToolApprovalRequestContent>()
                .ToList();
            if (pendingApprovals.Count > 0)
            {
                var toolNames = string.Join(", ",
                    pendingApprovals
                        .Select(request => (request.ToolCall as FunctionCallContent)?.Name ?? "unknown")
                        .Distinct(StringComparer.Ordinal));
                _logger.LogWarning(
                    "Forked agent '{Label}' requested approval for tool(s) [{Tools}]; non-interactive fork cannot grant approval",
                    run.Label, toolNames);
                ReportProgress(SubagentProgressPhase.Failed);
                return new ForkedAgentResult
                {
                    Messages = [],
                    Error = AgentProblemDetails.ToolExecutionFailed(
                        detail: $"Sub-agent requested approval for tool(s) [{toolNames}], which is not supported in a non-interactive fork.",
                        toolName: run.Label),
                };
            }

            var result = new ForkedAgentResult
            {
                Text = finalText,
                Messages = response.Messages.ToList(),
                // 非流式 GetResponseAsync 的 Usage 是工具循环所有轮次的累计值（M.E.AI 契约），
                // 不是末轮增量——直接取单值不会低估多轮 fork 的 token 消耗。
                TotalInputTokens = (long)(response.Usage?.InputTokenCount ?? 0),
                TotalOutputTokens = (long)(response.Usage?.OutputTokenCount ?? 0),
                TurnCount = pipeline.Metrics.ModelCallCount,
                ToolCallCount = pipeline.Metrics.ToolCallCount,
                BudgetExhausted = budgetExhausted,
                ChangedFiles = transaction.GetModifiedFiles(),
            };

            _logger.LogDebug(
                "Forked agent '{Label}' completed: {ToolCalls} tool calls, {Input}+{Output} tokens",
                run.Label, pipeline.Metrics.ToolCallCount, result.TotalInputTokens, result.TotalOutputTokens);

            transaction.Commit();
            // 完成上报以 MAF 管道计数为准（sink 计数已含，取权威值防口径漂移）。
            toolCalls = pipeline.Metrics.ToolCallCount;
            ReportProgress(SubagentProgressPhase.Completed, turns: result.TurnCount);
            return result;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // 外部令牌未取消 = 墙钟超时触发。结果不带 Error（父代理收 partial 而非失败），
            // TimedOut 供 RunAsync 标记 MaxTurnsReached 并让 AgentTool 标注 partial。
            _logger.LogWarning(
                "Forked agent '{Label}' timed out after {Timeout}ms", run.Label, parameters.Timeout?.TotalMilliseconds);
            ReportProgress(SubagentProgressPhase.Failed);
            return new ForkedAgentResult
            {
                Messages = [],
                ToolCallCount = toolCalls,
                TurnCount = CurrentTurns(),
                TimedOut = true,
            };
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Forked agent '{Label}' was cancelled", run.Label);
            ReportProgress(SubagentProgressPhase.Failed);
            return new ForkedAgentResult { Messages = [] };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Forked agent '{Label}' failed", run.Label);
            ReportProgress(SubagentProgressPhase.Failed);
            var problemDetails = AgentProblemDetails.ToolExecutionFailed(
                detail: ex.Message,
                toolName: parameters.ForkLabel);
            return new ForkedAgentResult
            {
                Messages = [],
                Error = problemDetails,
            };
        }
        finally
        {
            _activeRuns.TryRemove(runId, out _);
            cts.Dispose();
        }
    }

    public int ActiveRunCount => _activeRuns.Count;

    private const string WrapUpInstruction =
        "[Turn budget exhausted] Do NOT call any more tools. " +
        "Summarize what you found and did so far, and list what remains unfinished.";

    /// <summary>
    /// 撞上预算上限且最后一次迭代没有文本时补一次无工具收尾调用：父代理拿到的 partial
    /// 至少带有已完成工作的总结，而不是空输出。收尾失败不影响 partial 本身，返回 null。
    /// </summary>
    private async Task<string?> RunWrapUpAsync(AIAgent agent, AgentSession session, string label, CancellationToken ct)
    {
        try
        {
            var options = new ChatClientAgentRunOptions(new ChatOptions { ToolMode = ChatToolMode.None });
            var response = await agent.RunAsync(
                [new ChatMessage(ChatRole.User, WrapUpInstruction)], session, options, ct).ConfigureAwait(false);
            return response.Text;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Forked agent '{Label}' wrap-up call failed; returning partial without summary", label);
            return null;
        }
    }

    /// <summary>
    /// fork 的上下文提供器选项。WorkingMode 取模式控制器当前值：共享 provider 按
    /// 模式在装配期自检剔除（Plan 下 CodeAct/execute_code 不进子代理工具列表），
    /// 否则 Plan 模式中 fork 的 worker 仍带 execute_code，模式约束只剩权限层事后拒绝。
    /// </summary>
    internal AgentContextProviderOptions BuildContextProviderOptions(ForkedAgentParams parameters, string cwd)
        => new()
        {
            WorkingDirectory = cwd,
            ConversationId = parameters.ConversationId,
            WorkingMode = _modeController.Mode,
        };

    /// <summary>
    /// 子代理工具面：父级 AllowedToolNames 收敛 + 摘除派工工具。所有 fork 都是主 agent 的
    /// 子代理（GOAL 子目标不经此 runner），无条件排除 <see cref="ToolCapabilitySet.SubAgentDispatchTools"/>
    /// 即等价于「嵌套派工深度上限 1」，无需引入 Depth 环境流。
    /// </summary>
    internal static List<AITool> FilterChildTools(IEnumerable<AITool>? source, ToolCapabilitySet? capabilities)
        => source?
            .Where(tool => capabilities?.AllowedToolNames.Contains(tool.Name) != false)
            .Where(tool => !ToolCapabilitySet.SubAgentDispatchTools.Contains(tool.Name))
            .ToList() ?? [];

    /// <summary>自定义代理 allowed_tools 与父级能力取交集（只减不增）；无自定义限制时原样返回。</summary>
    internal static ToolCapabilitySet? ApplyCustomAllowedTools(
        ToolCapabilitySet? capabilities, IReadOnlyList<string>? customAllowedTools)
    {
        if (customAllowedTools is not { Count: > 0 })
            return capabilities;
        var customSet = ToolCapabilitySet.CreateUnrestricted(customAllowedTools);
        return capabilities is null ? customSet : capabilities.Intersect(customSet);
    }

    /// <summary>
    /// allowed_tools 与父级能力交集为空 = 该 fork 将零工具运行，必为配置错误（拼错工具名或
    /// 与父级能力完全不相交）。返回拒绝结果；null 即放行。这是派工入口未知名校验的兜底——
    /// 静默产出无工具子代理是最难自诊断的一类故障。
    /// </summary>
    internal static AgentRunResult? RejectEmptyAllowedToolSet(
        ToolCapabilitySet? capabilities, IReadOnlyList<string>? allowedToolNames, string agentName)
    {
        if (allowedToolNames is not { Count: > 0 } || capabilities is not { AllowedToolNames.Count: 0 })
            return null;
        return new AgentRunResult(
            Agent: agentName,
            ConversationId: SessionId.NewId(),
            Output: "",
            TurnsCompleted: 0,
            MaxTurnsReached: false,
            Error: AgentProblemDetails.ToolExecutionFailed(
                detail: $"allowed_tools [{string.Join(", ", allowedToolNames)}] intersected with parent capabilities is empty; " +
                        "check tool name spelling (unknown names are silently filtered out of the fork's tool set).",
                toolName: agentName));
    }

    public async Task<AgentRunResult> RunAsync(AgentRunRequest request, CancellationToken ct = default)
    {
        var profile = PipelineProfileBehavior.FromAgentType(request.Agent);
        var roleInstruction = PipelineProfileBehavior.GetRoleInstruction(profile);

        // 片段是**所有**子 Agent 的公共输入：父级 CacheSafeParams.SystemPrompt 只承载
        // 主 Agent 正文，不再包含片段，所以 Worker 分支不能靠继承父级 system 消息拿到它。
        // 只有角色 overlay 是 Explore/Plan 专属；两段保持分离，合成交给 MAF。
        // 自定义代理（.onecode/agents/*.md）的定义正文接管角色 overlay。
        var harnessInstructions = await _promptComposer.GetHarnessAsync(ct).ConfigureAwait(false);
        var agentInstructions = request.Instructions
            ?? (roleInstruction is null ? null : _promptComposer.RenderRoleBody(roleInstruction));

        var requestedCapabilities = request.CacheSafeParams?.ToolCapabilities;
        var capabilities = request.ParentCapabilities is not null && requestedCapabilities is not null
            ? request.ParentCapabilities.Intersect(requestedCapabilities)
            : request.ParentCapabilities ?? requestedCapabilities;
        capabilities = ApplyCustomAllowedTools(capabilities, request.AllowedToolNames);
        if (RejectEmptyAllowedToolSet(capabilities, request.AllowedToolNames, request.Agent) is { } rejection)
        {
            _logger.LogWarning("Forked agent '{Agent}' rejected: {Detail}", request.Agent, rejection.Error!.Detail);
            return rejection;
        }

        var (maxTurns, timeout) = ResolveLimits(profile, request);

        var parameters = new ForkedAgentParams
        {
            PromptMessages = [new ChatMessage(ChatRole.User, request.Prompt)],
            AgentInstructions = agentInstructions,
            // Unconditional: the fragment is not part of the inherited cache-safe prompt any more,
            // so a null here would silently degrade Worker forks to MAF's generic default text.
            HarnessInstructions = harnessInstructions,
            ForkLabel = request.Agent,
            Profile = profile,
            MaxTurns = maxTurns,
            Timeout = timeout,
            CacheSafeParams = request.CacheSafeParams,
            AllowedTools = profile is PipelineProfile.Explore or PipelineProfile.Plan
                ? PipelineProfileBehavior.ReadOnlyAgentTools
                : null,
            Capabilities = capabilities,
            ParentWorkingDirectory = request.WorkingDirectory,
        };

        var result = await RunForkedAgentAsync(parameters, ct).ConfigureAwait(false);

        if (result.Error is not null)
        {
            _logger.LogWarning(
                "Forked agent '{Agent}' returned problem details: {Type} - {Detail} (traceId={TraceId})",
                request.Agent, result.Error.Type, result.Error.Detail, result.Error.TraceId ?? "(none)");
        }

        // partial 判定：墙钟超时 / 轮预算耗尽（真实模型往返用掉收尾迭代）/ 工具安全上限触发。
        // 并行工具调用或高效的多步检索不会因工具数多而被误判。
        var maxTurnsReached = result.TimedOut || result.BudgetExhausted;

        return new AgentRunResult(
            Agent: request.Agent,
            ConversationId: SessionId.NewId(),
            Output: result.Text ?? "",
            TurnsCompleted: result.TurnCount,
            MaxTurnsReached: maxTurnsReached,
            Error: result.Error,
            ChangedFiles: result.ChangedFiles,
            InputTokens: result.TotalInputTokens,
            OutputTokens: result.TotalOutputTokens,
            ToolCallCount: result.ToolCallCount);
    }

    /// <summary>
    /// 解析一次子代理派工的全部上限（轮数 + 墙钟超时）。轮数走
    /// <see cref="ResolveEffectiveMaxTurns"/> 的上限链；超时读
    /// agents.defaults.timeoutMinutes（≤0 = 不限制）。
    /// </summary>
    internal (int MaxTurns, TimeSpan? Timeout) ResolveLimits(PipelineProfile profile, AgentRunRequest request)
    {
        var configured = _configManager.Current.Effective.Get(CoreConstants.ConfigKeys.AgentsDefaultMaxTurns, 0);
        return (ResolveEffectiveMaxTurns(profile, request, configured), ResolveSubAgentTimeout());
    }

    /// <summary>
    /// 生效轮数上限 = min(调用方请求值, 上限链)。上限链：frontmatter（MaxTurnsCeiling）>
    /// settings agents.defaults.maxTurns（≤0 = 未配置）> profile 内置默认。请求值只能收窄。
    /// </summary>
    internal static int ResolveEffectiveMaxTurns(
        PipelineProfile profile,
        AgentRunRequest request,
        int configuredMaxTurns)
    {
        var ceiling = ResolveMaxTurns(profile, request.MaxTurnsCeiling, configuredMaxTurns);
        return request.MaxTurns is { } requested and > 0
            ? Math.Min(requested, ceiling)
            : ceiling;
    }

    /// <summary>墙钟超时（agents.defaults.timeoutMinutes，≤0 = 不限制）。</summary>
    private TimeSpan? ResolveSubAgentTimeout()
    {
        var minutes = _configManager.Current.Effective.Get(
            CoreConstants.ConfigKeys.AgentsDefaultTimeoutMinutes,
            CoreConstants.Session.SubAgentTimeoutMinutesDefault);
        return minutes > 0 ? TimeSpan.FromMinutes(minutes) : null;
    }

    private sealed class ForkedAgentRun
    {
        public string Id { get; init; } = "";
        public string Label { get; init; } = "";
        public DateTimeOffset StartedAt { get; init; }
        public CancellationTokenSource? CancellationTokenSource { get; set; }
    }
}
