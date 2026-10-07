using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OneCode.Core.Errors;
using OneCode.Core.Models;
using OneCode.Infrastructure.Agent;
using OneCode.Infrastructure.Workflows;

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
    private readonly PromptComposer _promptComposer = promptComposer;
    private readonly ConcurrentDictionary<string, ForkedAgentRun> _activeRuns = new();

    public async Task<ForkedAgentResult> RunForkedAgentAsync(
        ForkedAgentParams parameters,
        CancellationToken ct = default)
    {
        var runId = Guid.NewGuid().ToString("N")[..8];
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var run = new ForkedAgentRun
        {
            Id = runId,
            Label = parameters.ForkLabel ?? "unnamed",
            StartedAt = DateTimeOffset.UtcNow,
            CancellationTokenSource = cts,
        };

        _activeRuns[runId] = run;

        try
        {
            _logger.LogDebug("Starting forked agent '{Label}' (id={RunId})", run.Label, runId);

            using var transaction = new EditTransaction(
                _loggerFactory.CreateLogger<EditTransaction>());

            // fork 写入 intent 先于任何文件写入持久化；并发 fork 间不做 reconcile
            //（会互踩彼此刚持久化的 intent），世代清扫由 step 级 BeginAsync 独占。
            if (parameters.LedgerScope is { } ledgerScope)
            {
                await FencedLedgerTransaction.BeginForkAsync(
                    ledgerScope.Ledger, ledgerScope, transaction, ct).ConfigureAwait(false);
            }

            var csp = parameters.CacheSafeParams;
            var maxTurns = parameters.MaxTurns ?? 10;
            var tools = FilterChildTools(csp?.Tools, parameters.Capabilities);
            // cwd 决策：显式覆盖（GOAL 隔离 worktree 等）优先，否则会话工作目录访问器。
            // ParentWorkingDirectory 此前声明但从未被消费，导致 GOAL worktree 中派生的
            // 子代理落在会话根目录——现在生效。
            var cwd = parameters.ParentWorkingDirectory ?? _workingDirectoryAccessor.WorkingDirectory;
            var profile = parameters.Profile;

            var contextProviders = _contextPipeline.BuildShared(
                profile,
                new AgentContextProviderOptions
                {
                    WorkingDirectory = cwd,
                    ConversationId = parameters.ConversationId,
                });

            _logger.LogInformation(
                "Forked agent '{Label}' (profile={Profile}) armed with {Count} tools, cwd={Cwd}",
                parameters.ForkLabel, profile, tools.Count, cwd);

            var providerId = csp?.ModelId is null ? null : _modelManager.Resolve(csp.ModelId)?.ProviderId;
            var linkedToken = cts.Token;

            var pipelineOptions = _pipelineFactory.BuildOptions(new SubAgentPipelineRequest
            {
                Profile = profile,
                WorkingDirectory = cwd,
                EditTransaction = transaction,
                MaxToolCalls = maxTurns,
                ModelId = csp?.ModelId,
                ProviderId = providerId,
                // usage 归属当前会话（UsageTrackingRunMiddleware 据此写会话桶，/status 才能看到
                // 子代理消耗）；上下文提供器仍取 parameters.ConversationId（fork 本地，
                // 派工路径均为 null），子代理不得读/写主会话的转录与设计上下文。
                ConversationId = _sessionIds.CurrentSessionId,
                AllowedTools = parameters.AllowedTools,
            });

            var pipeline = AgentPipelineBuilder.BuildHarnessAgent(new ChatClientAgentBuildOptions
            {
                ChatClient = _chatClient,
                Name = parameters.ForkLabel ?? "sub-agent",
                ChatOptions = new ChatOptions
                {
                    ModelId = csp?.ModelId,
                    MaxOutputTokens = parameters.MaxOutputTokens ?? 4096,
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
                Text = response.Text,
                Messages = response.Messages.ToList(),
                // 非流式 GetResponseAsync 的 Usage 是工具循环所有轮次的累计值（M.E.AI 契约），
                // 不是末轮增量——直接取单值不会低估多轮 fork 的 token 消耗。
                TotalInputTokens = (long)(response.Usage?.InputTokenCount ?? 0),
                TotalOutputTokens = (long)(response.Usage?.OutputTokenCount ?? 0),
                TurnCount = 1 + pipeline.Metrics.ToolCallCount,
                ChangedFiles = transaction.GetModifiedFiles(),
            };

            _logger.LogDebug(
                "Forked agent '{Label}' completed: {ToolCalls} tool calls, {Input}+{Output} tokens",
                run.Label, pipeline.Metrics.ToolCallCount, result.TotalInputTokens, result.TotalOutputTokens);

            // 账本提交先于 fork 事务提交——「回执存在 ⇒ ledger 已提交」的 fork 级镜像。
            if (parameters.LedgerScope is { } commitScope)
            {
                await commitScope.Ledger.CommitTransactionAsync(
                    commitScope.OperationId,
                    commitScope.FencingToken,
                    $"fork:{parameters.ForkLabel}",
                    ct).ConfigureAwait(false);
            }

            transaction.Commit();
            return result;
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Forked agent '{Label}' was cancelled", run.Label);
            return new ForkedAgentResult { Messages = [] };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Forked agent '{Label}' failed", run.Label);
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

        // 片段是**所有**子 Agent 的公共输入：父级 CacheSafeParams.SystemPrompt 自 §4.6 起只承载
        // 主 Agent 正文，不再包含片段，所以 Worker 分支不能靠继承父级 system 消息拿到它。
        // 只有角色 overlay 是 Explore/Plan 专属；两段保持分离，合成交给 MAF。
        // 自定义代理（.onecode/agents/*.md）的定义正文接管角色 overlay。
        var harnessInstructions = await _promptComposer.GetHarnessAsync(ct).ConfigureAwait(false);
        var agentInstructions = request.Instructions
            ?? (roleInstruction is null ? null : _promptComposer.RenderRoleBody(roleInstruction));

        var childAllowedTools = profile is PipelineProfile.Explore or PipelineProfile.Plan
            ? PipelineProfileBehavior.ReadOnlyAgentTools
            : request.CacheSafeParams?.Tools?.Select(tool => tool.Name).ToList();
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

        var parameters = new ForkedAgentParams
        {
            LedgerScope = request.LedgerScope,
            PromptMessages = [new ChatMessage(ChatRole.User, request.Prompt)],
            AgentInstructions = agentInstructions,
            // Unconditional: the fragment is not part of the inherited cache-safe prompt any more,
            // so a null here would silently degrade Worker forks to MAF's generic default text.
            HarnessInstructions = harnessInstructions,
            ForkLabel = request.Agent,
            Profile = profile,
            MaxTurns = request.MaxTurns ?? 50,
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

        return new AgentRunResult(
            Agent: request.Agent,
            ConversationId: SessionId.NewId(),
            Output: result.Text ?? "",
            TurnsCompleted: result.TurnCount,
            MaxTurnsReached: result.TurnCount >= (parameters.MaxTurns ?? 50),
            Error: result.Error,
            ChangedFiles: result.ChangedFiles,
            InputTokens: result.TotalInputTokens,
            OutputTokens: result.TotalOutputTokens);
    }

    private sealed class ForkedAgentRun
    {
        public string Id { get; init; } = "";
        public string Label { get; init; } = "";
        public DateTimeOffset StartedAt { get; init; }
        public CancellationTokenSource? CancellationTokenSource { get; set; }
    }
}
