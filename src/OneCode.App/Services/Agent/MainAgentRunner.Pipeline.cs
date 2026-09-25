using OneCode.Infrastructure.Ai;
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
    private async Task<AgentPipelineHandle> BuildAgentPipelineAsync(
        MainAgentRunOptions options,
        EditTransaction transaction,
        string cwd,
        CancellationToken ct = default)
    {
        var (compactionStrategy, providerId) = await _compactionBuilder
            .BuildAsync(options.ModelId, ct).ConfigureAwait(false);

        var contextProviders = await _contextPipeline
            .BuildForMainAsync(new AgentContextProviderOptions
            {
                WorkingDirectory = cwd,
                ConversationId = options.ConversationId,
            }, options.WorkingMode, ct).ConfigureAwait(false);

        var pipelineOptions = _pipelineAssembly.BuildMainOptions(
            options, transaction, cwd, options.ModelId, providerId);

        var handle = AgentPipelineBuilder.BuildHarnessAgent(new ChatClientAgentBuildOptions
        {
            ChatClient = new MaxOutputTokensDecorator(_chatClient),
            Name = "main-agent",
            ChatOptions = BuildChatOptions(options),
            LoggerFactory = _loggerFactory,
            ServiceProvider = _serviceProvider,
            ToolMetadata = _toolMetadata,
            CompactionStrategy = compactionStrategy,
            // 多轮历史经 MAF chat history 契约（桥接会话转录）提供，不由宿主注入请求消息。
            ChatHistoryProvider = options.ConversationId is { } conversationId
                ? _sessionStore.CreateChatHistoryProvider(conversationId)
                : null,
            HarnessInstructions = options.HarnessInstructions,
            AgentContextProviders = contextProviders,
            PipelineOptions = pipelineOptions,
        });

        // MAF does not dispose the context providers it is handed, and some of them are built per run
        // (skills, so a server that connected in the background is visible next run). The lease gives
        // the runner an owner that releases them after the last use.
        handle.ContextProviderLease = AgentContextProviderLease.Track(contextProviders);
        return handle;
    }
}
