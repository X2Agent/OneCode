using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OneCode.App.Services.Compact;
using OneCode.Core.Models;
using OneCode.Infrastructure.Agent;

namespace OneCode.App.Services.Agent;

/// <summary>
/// 构建压缩管道 ContextProvider。
/// 按模型上下文窗口比例计算阈值，自动适配不同模型（32K ~ 1M+）。
/// 摘要 prompt 经 <see cref="CompactPromptBuilder"/> 统一加载（system/compact + 内置兜底）。
/// </summary>
public sealed class CompactionProviderBuilder
{
    private readonly IChatClient _chatClient;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IModelManager _modelManager;
    private readonly CompactPromptBuilder _compactPromptBuilder;

    public CompactionProviderBuilder(
        IChatClient chatClient,
        ILoggerFactory loggerFactory,
        IModelManager modelManager,
        CompactPromptBuilder compactPromptBuilder)
    {
        _chatClient = chatClient;
        _loggerFactory = loggerFactory;
        _modelManager = modelManager;
        _compactPromptBuilder = compactPromptBuilder;
    }

    /// <summary>
    /// 构建主 Agent 压缩管道 Provider，并返回模型 ProviderId（供 PipelineSecurityContext 使用）。
    /// </summary>
    public async Task<(AIContextProvider CompactionProvider, string? ProviderId)> BuildAsync(
        string? modelId,
        CancellationToken ct)
    {
        var modelInfo = modelId is null
            ? null
            : _modelManager.Resolve(modelId);

        var maxContextWindow = modelInfo?.ContextWindow ?? ModelContextDefaults.Resolve(modelId);
        var maxOutputTokens = modelInfo?.MaxOutputTokens > 0 ? modelInfo.MaxOutputTokens : 8192;
        var summarizationPrompt = await LoadSummarizationPromptAsync(ct).ConfigureAwait(false);
        var compactionProvider = CompactionPipelineBuilder.BuildForMainAgent(
            _chatClient, _loggerFactory, maxContextWindow, maxOutputTokens, summarizationPrompt);

        return (compactionProvider, modelInfo?.ProviderId);
    }

    /// <summary>
    /// 构建 Worker / Forked / Team 子 Agent 压缩管道（更激进阈值 + 同一摘要 prompt）。
    /// </summary>
    public async Task<AIContextProvider> BuildForWorkerAsync(
        string? modelId,
        int? maxOutputTokensOverride,
        CancellationToken ct)
    {
        var modelInfo = modelId is null
            ? null
            : _modelManager.Resolve(modelId);

        var maxContextWindow = modelInfo?.ContextWindow ?? ModelContextDefaults.Resolve(modelId);
        var maxOutputTokens = maxOutputTokensOverride
            ?? (modelInfo?.MaxOutputTokens > 0 ? modelInfo.MaxOutputTokens : 4096);
        var summarizationPrompt = await LoadSummarizationPromptAsync(ct).ConfigureAwait(false);

        return CompactionPipelineBuilder.BuildForWorkerAgent(
            _chatClient, _loggerFactory, maxContextWindow, maxOutputTokens, summarizationPrompt);
    }

    private async Task<string> LoadSummarizationPromptAsync(CancellationToken ct)
    {
        try
        {
            return await _compactPromptBuilder.GetSummarizationPromptAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _loggerFactory.CreateLogger<CompactionProviderBuilder>()
                .LogDebug(ex, "Failed to load system/compact prompt; using built-in fallback");
            return CompactPromptBuilder.FallbackCompactPrompt;
        }
    }
}
