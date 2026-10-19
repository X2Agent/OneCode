using Microsoft.Agents.AI.Compaction;
using Microsoft.Extensions.AI;
using OneCode.App.Services.Compact;
using OneCode.Core.Models;
using OneCode.Infrastructure.Agent;

namespace OneCode.App.Services.Agent;

/// <summary>
/// 构建产品压缩策略。
/// 按模型上下文窗口比例计算阈值，自动适配不同模型（32K ~ 1M+）。
/// 摘要 prompt 经 <see cref="CompactPromptBuilder"/> 统一加载（system/compact）；
/// 缺失时异常向上传播，在 Agent 管道构建期 fail-fast。
///
/// <para>产出的是<b>策略</b>而非 provider（故不叫 ProviderBuilder）：策略交给
/// <c>HarnessAgentOptions.CompactionStrategy</c>，由 Harness 自行挂载唯一的 provider。
/// 产品侧再挂一份会让同一次模型调用被压缩两次，且外层 provider 会覆盖内层的会话状态。</para>
/// </summary>
public sealed class CompactionStrategyFactory(
    IChatClient chatClient,
    IModelManager modelManager,
    CompactPromptBuilder compactPromptBuilder)
{
    private readonly IChatClient _chatClient = chatClient;
    private readonly IModelManager _modelManager = modelManager;
    private readonly CompactPromptBuilder _compactPromptBuilder = compactPromptBuilder;

    /// <summary>
    /// 构建主 Agent 压缩策略，并返回模型 ProviderId。
    /// </summary>
    public async Task<(CompactionStrategy CompactionStrategy, string? ProviderId)> BuildAsync(
        string? modelId,
        CancellationToken ct)
    {
        var modelInfo = modelId is null
            ? null
            : _modelManager.Resolve(modelId);

        var maxContextWindow = ResolveContextWindow(modelInfo, modelId);
        var maxOutputTokens = TokenBudget.ResolveOutputReservation(
            modelInfo,
            maxContextWindow,
            modelInfo?.MaxOutputTokens > 0 ? modelInfo.MaxOutputTokens : 8192);
        var summarizationPrompt = await _compactPromptBuilder
            .GetSummarizationPromptAsync(ct).ConfigureAwait(false);
        var compactionStrategy = CompactionPipelineBuilder.BuildForMainAgent(
            _chatClient, maxContextWindow, maxOutputTokens, summarizationPrompt);

        return (compactionStrategy, modelInfo?.ProviderId);
    }

    /// <summary>
    /// 构建 Worker / Forked 子 Agent 压缩策略（更激进阈值 + 同一摘要 prompt）。
    /// <paramref name="readOnlyResearch"/> 为 true（Explore / Plan）时叠加绝对 token 上限，限制单个调研子代理的上下文膨胀。
    /// </summary>
    public async Task<CompactionStrategy> BuildForWorkerAsync(
        string? modelId,
        int? maxOutputTokensOverride,
        bool readOnlyResearch,
        CancellationToken ct)
    {
        var modelInfo = modelId is null
            ? null
            : _modelManager.Resolve(modelId);

        var maxContextWindow = ResolveContextWindow(modelInfo, modelId);
        var maxOutputTokens = TokenBudget.ResolveOutputReservation(
            modelInfo,
            maxContextWindow,
            maxOutputTokensOverride ?? (modelInfo?.MaxOutputTokens > 0 ? modelInfo.MaxOutputTokens : 4096));
        var summarizationPrompt = await _compactPromptBuilder
            .GetSummarizationPromptAsync(ct).ConfigureAwait(false);

        return CompactionPipelineBuilder.BuildForWorkerAgent(
            _chatClient, maxContextWindow, maxOutputTokens, summarizationPrompt, readOnlyResearch);
    }

    /// <summary>
    /// 解析上下文窗口。<c>0</c> 不是「未配置」——必须与 <c>TokenBudget.GetMaxContextTokens</c>
    /// 同款守卫式读取，否则 0 会被当作有效窗口传给严格校验的 <c>ResolveInputBudget</c>，
    /// 在管道构建期直接抛错。
    /// </summary>
    private static int ResolveContextWindow(ModelInfo? modelInfo, string? modelId)
        => modelInfo is { ContextWindow: > 0 } mi ? mi.ContextWindow : ModelContextDefaults.Resolve(modelId);
}
