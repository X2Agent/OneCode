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
        var (compactionProvider, providerId) = await _compactionBuilder
            .BuildAsync(options.ModelId, ct).ConfigureAwait(false);

        var contextProviders = await _mainContextBuilder
            .BuildForMainAsync(new AgentContextProviderOptions
            {
                WorkingDirectory = cwd,
                ChatClient = _chatClient,
                ConversationId = options.ConversationId,
                CodeActTools = options.Tools?.ToList(),
            }, options.WorkingMode, ct).ConfigureAwait(false);

        var pipelineOptions = _pipelineAssembly.BuildMainOptions(
            options, transaction, cwd, options.ModelId, providerId);

        return AgentPipelineBuilder.BuildChatClientAgent(new ChatClientAgentBuildOptions
        {
            ChatClient = new MaxOutputTokensDecorator(_chatClient),
            Name = "main-agent",
            ChatOptions = BuildChatOptions(options),
            LoggerFactory = _loggerFactory,
            ServiceProvider = _serviceProvider,
            ChatClientContextProviders = [compactionProvider],
            AgentContextProviders = contextProviders,
            ToolMetadata = _toolMetadata,
            PipelineOptions = pipelineOptions,
        });
    }
}
