using OneCode.Infrastructure.Agent;

namespace OneCode.App.Services.Agent;

/// <summary>
/// <see cref="MainAgentRunner"/> 的管道构建部分。
/// </summary>
public partial class MainAgentRunner
{
    /// <summary>
    /// Builds the ChatClientAgent and the shared MAF middleware pipeline.
    /// Shared by RunAsync and RunStreamingAsync.
    /// </summary>
    /// <param name="options">主代理运行选项。</param>
    /// <param name="transaction">本轮编辑事务。</param>
    /// <param name="cwd">工作目录。</param>
    /// <param name="budgetBaselineTokens">
    /// 显式预算基线；null 表示就地快照 ledger。返回值携带实际使用的基线，
    /// 供流式零输出重试复用（首轮零输出 attempt 的消耗不得逃出 BUILD 预算）。
    /// </param>
    /// <param name="ct">取消令牌。</param>
    private async Task<BuiltPipeline> BuildAgentPipelineAsync(
        MainAgentRunOptions options,
        EditTransaction transaction,
        string cwd,
        long? budgetBaselineTokens = null,
        CancellationToken ct = default)
    {
        var (compactionStrategy, providerId) = await _compactionBuilder
            .BuildAsync(options.ModelId, ct).ConfigureAwait(false);

        var contextProviders = await _contextPipeline
            .BuildForMainAsync(new AgentContextProviderOptions
            {
                WorkingDirectory = cwd,
                ConversationId = options.ConversationId,
                SuppressToolApproval = options.SuppressToolApproval,
            }, options.WorkingMode, ct).ConfigureAwait(false);

        var pipelineOptions = _pipelineAssembly.BuildMainOptions(
            options, transaction, cwd, options.ModelId, providerId, budgetBaselineTokens);

        var handle = AgentPipelineBuilder.BuildHarnessAgent(new ChatClientAgentBuildOptions
        {
            ChatClient = _chatClient,
            Name = MainAgentName,
            ChatOptions = BuildChatOptions(options),
            LoggerFactory = _loggerFactory,
            ServiceProvider = _serviceProvider,
            ToolMetadata = _toolMetadata,
            CompactionStrategy = compactionStrategy,
            // 多轮历史经 MAF chat history 契约（桥接会话转录）提供，不由宿主注入请求消息。
            ChatHistoryProvider = options.ConversationId is { } conversationId
                ? _sessionStore.CreateChatHistoryProvider(
                    conversationId,
                    options.HistorySeedBoundary,
                    stripPlanExclusiveTools: options.WorkingMode != WorkingMode.Plan)
                : null,
            HarnessInstructions = options.HarnessInstructions,
            AgentContextProviders = contextProviders,
            PipelineOptions = pipelineOptions,
        });

        // MAF does not dispose the context providers it is handed, and some of them are built per run
        // (skills, so a server that connected in the background is visible next run). The lease gives
        // the runner an owner that releases them after the last use.
        handle.ContextProviderLease = AgentContextProviderLease.Track(contextProviders);
        return new BuiltPipeline(handle, pipelineOptions.BudgetBaselineTokens);
    }

    /// <summary>构建产物：管道句柄 + 本轮实际生效的预算基线（供重试复用）。</summary>
    private sealed record BuiltPipeline(AgentPipelineHandle Handle, long BudgetBaselineTokens);
}
