using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OneCode.App.Services.Agent;
using OneCode.Core.Coordinator;
using OneCode.Core.Models;
using OneCode.Core.Prompt;
using OneCode.Infrastructure.Ai;
using OneCode.Infrastructure.Agent;

namespace OneCode.App.Services.Coordinator;

/// <summary>
/// Agent factory extracted from TeamOrchestrationService.
///
/// 职责：为 Team 成员构建 MAF <see cref="AIAgent"/>，包括：
///   - 角色 prompt 解析（YAML 内联 → system/{role}.prompt → 兜底）
///   - 工具集装配（toolsAccessor fallback → ToolCatalog → AllowedTools 过滤）
///   - ContextProvider 装配（通用 provider；角色正文改由 ChatOptions.Instructions 承载）
///   - Pipeline 配置（AgentPipelineOptionsFactory；审批经 MAF 协议，由 TeamWorkflowRunner 桥接）
///
/// 管道配置与 MainAgentRunner.BuildAgentPipeline 对齐。
/// </summary>
internal sealed class TeamAgentFactory(
    IChatClient chatClient,
    ILoggerFactory loggerFactory,
    ILogger<TeamAgentFactory> logger,
    IServiceProvider serviceProvider,
    IModelManager modelManager,
    IPromptManager promptManager,
    PromptComposer promptComposer,
    TeamAgentToolSources toolSources,
    TeamAgentPipelineDependencies pipelineDeps)
{
    private readonly ConcurrentDictionary<string, string> _rolePromptCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Team 当前使用的主模型 Id（DefinitionHash / 定义一致性用）。
    /// Team 成员统一使用主模型，故用 GetMainModel().Id 作为稳定模型标识。
    /// </summary>
    public string? MainModelId => modelManager?.GetMainModel()?.Id;

    /// <summary>
    /// 为 Team 成员构建 MAF AIAgent。
    /// </summary>
    public async Task<AIAgent> BuildAgentAsync(
        TeamMember member,
        EditTransaction? transaction = null,
        string? workingDirectory = null,
        Action<OrchestrationEvent>? eventSink = null,
        IReadOnlyList<string>? taskAllowedTools = null)
    {
        var roleBody = await ResolveRolePromptAsync(member).ConfigureAwait(false);
        // Two halves, kept apart: MAF composes the harness fragment ahead of the role body.
        var harnessInstructions = await promptComposer.GetHarnessAsync(CancellationToken.None)
            .ConfigureAwait(false);
        var systemPrompt = promptComposer.RenderRoleBody(roleBody);

        // Prefer parent query tool snapshot; fall back to full catalog before first query.
        List<AIFunction>? toolList = toolSources.CacheSafeParams.Current?.Tools?.OfType<AIFunction>().ToList();
        if (toolList is null || toolList.Count == 0)
        {
            toolList = toolSources.ToolCatalog.Tools.OfType<AIFunction>().ToList();
            if (toolList is { Count: > 0 })
                logger.LogDebug("CacheSafeParams tools empty, fell back to ToolCatalog ({Count} tools)", toolList.Count);
        }

        var effectiveAllowedTools = IntersectAllowedTools(member.AllowedTools, taskAllowedTools);

        // Apply both the member policy and the approved task policy to the visible tools.
        if (effectiveAllowedTools is not null && toolList is { Count: > 0 })
        {
            var allowedSet = new HashSet<string>(effectiveAllowedTools, StringComparer.OrdinalIgnoreCase);
            toolList = toolList.Where(t => allowedSet.Contains(t.Name)).ToList();
            logger.LogDebug("Applied AllowedTools filter for {Agent}: {Count} tools remaining", member.AgentId, toolList.Count);
        }

        // ChatOptions.Tools 期望 IList<AITool>，AIFunction 继承 AITool，需显式 Cast
        IList<AITool>? tools = toolList?.Count > 0 ? toolList.Cast<AITool>().ToList() : null;

        var cwd = workingDirectory ?? Environment.CurrentDirectory;

        // 解析 Team 成员模型 limits：Team 成员通常使用主模型，阈值按其上下文窗口比例计算
        var teamModelInfo = modelManager?.GetMainModel();
        var compactionStrategy = await pipelineDeps.CompactionBuilder.BuildForWorkerAsync(
            teamModelInfo?.Id,
            maxOutputTokensOverride: null,
            CancellationToken.None).ConfigureAwait(false);
        var maxOutputDecorator = new MaxOutputTokensDecorator(chatClient);
        var instrumentedClient = new DiagnosticsChatClient(maxOutputDecorator, member.AgentId, logger);

        // MaxTurns 语义：TeamConfig.MaxTurns = 外层轮数；pipelineMaxToolCallsPerMember = 每 Agent 内部工具调用上限。
        const int pipelineMaxToolCallsPerMember = 50;

        var contextProviders = new List<AIContextProvider>();
        contextProviders.AddRange(pipelineDeps.ContextPipeline.BuildShared(
            PipelineProfile.TeamMember,
            new AgentContextProviderOptions { WorkingDirectory = cwd }));

        var pipelineOptions = pipelineDeps.PipelineFactory.BuildOptions(new SubAgentPipelineRequest
        {
            Profile = PipelineProfile.TeamMember,
            WorkingDirectory = cwd,
            EditTransaction = transaction,
            MaxToolCalls = pipelineMaxToolCallsPerMember,
            ProviderId = modelManager?.GetMainModel()?.ProviderId,
            ConversationId = pipelineDeps.SessionAccess.ForegroundConversation?.Id,
            OrchestrationEventSink = eventSink,
            FileChangeCallback = change => eventSink?.Invoke(new OrchestrationEvent.FileChanged(
                member.AgentId,
                change.FileName,
                change.AddedLines,
                change.RemovedLines)),
            TeamMemberId = member.AgentId,
            AllowedTools = effectiveAllowedTools,
        });

        return AgentPipelineBuilder.BuildChatClientAgent(new ChatClientAgentBuildOptions
        {
            ChatClient = instrumentedClient,
            Name = member.AgentId,
            ChatOptions = new ChatOptions
            {
                MaxOutputTokens = 4096,
                Tools = tools?.Count > 0 ? tools : null,
                ToolMode = tools?.Count > 0 ? ChatToolMode.Auto : null,
                // 角色正文走 MAF 的 agent-instructions 入口，与 harness 片段在 MAF 内合成。
                Instructions = string.IsNullOrWhiteSpace(systemPrompt) ? null : systemPrompt,
            },
            LoggerFactory = loggerFactory,
            ServiceProvider = serviceProvider,
            ToolMetadata = pipelineDeps.ToolMetadata,
            CompactionStrategy = compactionStrategy,
            // Shared harness fragment → MAF HarnessInstructions. Handing the fragment to MAF is what
            // keeps it from being dropped, then silently replaced by MAF's generic default instructions.
            HarnessInstructions = harnessInstructions,
            AgentContextProviders = contextProviders,
            PipelineOptions = pipelineOptions,
        }).Agent;
    }

    private static IReadOnlyList<string>? IntersectAllowedTools(
        IReadOnlyList<string>? memberAllowedTools,
        IReadOnlyList<string>? taskAllowedTools)
    {
        if (memberAllowedTools is null && taskAllowedTools is null)
            return null;

        if (memberAllowedTools is null)
            return taskAllowedTools;
        if (taskAllowedTools is null)
            return memberAllowedTools;

        var taskSet = taskAllowedTools.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return memberAllowedTools
            .Where(taskSet.Contains)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>
    /// 解析角色 prompt 正文（不含 shared harness）— 加载优先级：
    /// 1. member.SystemPrompt (YAML instructions 内联) → 非空则用
    /// 2. system/{role}.prompt (按角色名约定路径，orchestrator/lead → system/coordinator) → 文件存在则用
    /// 3. DefaultSystemPrompt(role) (极简兜底，仅用于自定义角色)
    /// 调用方经 <see cref="PromptComposer.GetHarnessAsync"/> + <see cref="PromptComposer.RenderRoleBody"/> 取得两段提示词；合成由 MAF 负责。
    /// </summary>
    private async Task<string> ResolveRolePromptAsync(TeamMember member)
    {
        if (!string.IsNullOrWhiteSpace(member.SystemPrompt))
            return member.SystemPrompt!;

        var role = member.Role ?? "general";

        var promptKey = role is "lead" or "orchestrator" ? "system/coordinator" : $"system/{role}";
        if (_rolePromptCache.TryGetValue(promptKey, out var cached))
            return cached;

        if (promptManager is not null)
        {
            var loaded = await promptManager.GetPromptAsync(promptKey, CancellationToken.None).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(loaded))
            {
                _rolePromptCache[promptKey] = loaded;
                return loaded;
            }
        }

        var baseline = DefaultSystemPrompt(role);
        _rolePromptCache[promptKey] = baseline;
        return baseline;
    }

    /// <summary>
    /// 极简角色兜底——仅用于自定义角色名（无对应 system/{role}.prompt 文件）。
    /// </summary>
    private static string DefaultSystemPrompt(string? role) =>
        $"You are a {role ?? "general"} agent. Complete your assigned tasks professionally.";

    /// <summary>
    /// TEAM 诊断装饰器：记录每次 LLM 调用的进入/退出与响应文本长度。
    /// 用于区分「模型从未被调用」与「调用返回空响应」两类静默失败（turns=0 问题定位）。
    /// </summary>
    public sealed class DiagnosticsChatClient : IChatClient
    {
        private readonly IChatClient _inner;
        private readonly string _agentId;
        private readonly ILogger<TeamAgentFactory> _logger;

        public DiagnosticsChatClient(IChatClient inner, string agentId, ILogger<TeamAgentFactory> logger)
        {
            _inner = inner;
            _agentId = agentId;
            _logger = logger;
        }

        public async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            _logger.LogInformation(
                "Team LLM call ENTER agent={Agent} messages={Count} ctCancelled={Cancelled}",
                _agentId, messages.Count(), cancellationToken.IsCancellationRequested);
            var response = await _inner.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation(
                "Team LLM call EXIT agent={Agent} textLen={Len} finish={Finish}",
                _agentId, response.Text?.Length ?? 0, response.FinishReason);
            return response;
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            _logger.LogInformation(
                "Team LLM stream ENTER agent={Agent} messages={Count} ctCancelled={Cancelled}",
                _agentId, messages.Count(), cancellationToken.IsCancellationRequested);
            var count = 0;
            await foreach (var update in _inner.GetStreamingResponseAsync(messages, options, cancellationToken).ConfigureAwait(false))
            {
                count++;
                yield return update;
            }
            _logger.LogInformation("Team LLM stream EXIT agent={Agent} updates={Count}", _agentId, count);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => _inner.GetService(serviceType, serviceKey);
        void IDisposable.Dispose() => (_inner as IDisposable)?.Dispose();
    }
}
