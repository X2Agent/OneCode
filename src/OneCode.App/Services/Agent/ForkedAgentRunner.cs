using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OneCode.Core.Errors;
using OneCode.Core.Models;
using OneCode.Infrastructure.Agent;

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
public sealed class ForkedAgentRunner : IAgentRunner
{
    private readonly ILogger<ForkedAgentRunner> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IChatClient _chatClient;
    // IServiceProvider 仅用于传递给 MAF 的 ChatClientAgentBuildOptions.ServiceProvider
    // （MAF 框架要求），不得用于业务逻辑中的 GetService<T>() 调用。
    private readonly IServiceProvider _serviceProvider;
    private readonly AgentContextPipeline _contextPipeline;
    private readonly SubAgentPipelineFactory _pipelineFactory;
    private readonly IModelManager _modelManager;
    private readonly IWorkingDirectoryAccessor _workingDirectoryAccessor;
    private readonly Core.Tools.ToolMetadataRegistry _toolMetadata;
    private readonly CompactionStrategyFactory _compactionBuilder;
    private readonly PromptComposer _promptComposer;
    private readonly ConcurrentDictionary<string, ForkedAgentRun> _activeRuns = new();

    public ForkedAgentRunner(
        ILogger<ForkedAgentRunner> logger,
        ILoggerFactory loggerFactory,
        IServiceProvider serviceProvider,
        AgentContextPipeline contextPipeline,
        SubAgentPipelineFactory pipelineFactory,
        ForkedAgentRuntimeDependencies runtime,
        PromptComposer promptComposer)
    {
        _logger = logger;
        _loggerFactory = loggerFactory;
        _serviceProvider = serviceProvider;
        _contextPipeline = contextPipeline;
        _pipelineFactory = pipelineFactory;
        _chatClient = runtime.ChatClient;
        _modelManager = runtime.ModelManager;
        _workingDirectoryAccessor = runtime.WorkingDirectory;
        _toolMetadata = runtime.ToolMetadata;
        _compactionBuilder = runtime.CompactionBuilder;
        _promptComposer = promptComposer;
    }

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

            var csp = parameters.CacheSafeParams;
            var maxTurns = parameters.MaxTurns ?? 10;
            var tools = csp?.Tools?
                .Where(tool => parameters.Capabilities?.AllowedToolNames.Contains(tool.Name) != false)
                .ToList() ?? (List<AITool>)[];
            var cwd = _workingDirectoryAccessor.WorkingDirectory;
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
                ConversationId = parameters.ConversationId,
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

            if (parameters.PromptMessages != null)
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
                TotalInputTokens = (long)(response.Usage?.InputTokenCount ?? 0),
                TotalOutputTokens = (long)(response.Usage?.OutputTokenCount ?? 0),
                TurnCount = 1 + pipeline.Metrics.ToolCallCount,
            };

            _logger.LogDebug(
                "Forked agent '{Label}' completed: {ToolCalls} tool calls, {Input}+{Output} tokens",
                run.Label, pipeline.Metrics.ToolCallCount, result.TotalInputTokens, result.TotalOutputTokens);

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

    public async Task<AgentRunResult> RunAsync(AgentRunRequest request, CancellationToken ct = default)
    {
        var profile = PipelineProfileBehavior.FromAgentType(request.Agent);
        var roleInstruction = PipelineProfileBehavior.GetRoleInstruction(profile);

        // 片段是**所有**子 Agent 的公共输入：父级 CacheSafeParams.SystemPrompt 自 §4.6 起只承载
        // 主 Agent 正文，不再包含片段，所以 Worker 分支不能靠继承父级 system 消息拿到它。
        // 只有角色 overlay 是 Explore/Plan 专属；两段保持分离，合成交给 MAF。
        var harnessInstructions = await _promptComposer.GetHarnessAsync(ct).ConfigureAwait(false);
        var agentInstructions = roleInstruction is null
            ? null
            : _promptComposer.RenderRoleBody(roleInstruction);

        var childAllowedTools = profile is PipelineProfile.Explore or PipelineProfile.Plan
            ? PipelineProfileBehavior.ReadOnlyAgentTools
            : request.CacheSafeParams?.Tools?.Select(tool => tool.Name).ToList();
        var requestedCapabilities = request.CacheSafeParams?.ToolCapabilities;
        var capabilities = request.ParentCapabilities is not null && requestedCapabilities is not null
            ? request.ParentCapabilities.Intersect(requestedCapabilities)
            : request.ParentCapabilities ?? requestedCapabilities;

        var parameters = new ForkedAgentParams
        {
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
            Error: result.Error);
    }

    private sealed class ForkedAgentRun
    {
        public string Id { get; init; } = "";
        public string Label { get; init; } = "";
        public DateTimeOffset StartedAt { get; init; }
        public CancellationTokenSource? CancellationTokenSource { get; set; }
    }
}
