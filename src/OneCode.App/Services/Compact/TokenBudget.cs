using OneCode.Core;
using OneCode.Core.Models;
using CoreConstants = OneCode.Core.Constants;

namespace OneCode.App.Services.Compact;

public static class TokenBudget
{
    private const int ReservedOutputTokens = 8_192;

    public static TokenBudgetStatus Estimate(
        Conversation session,
        ITokenEstimator tokenEstimator,
        string? systemPrompt = null,
        IModelManager? modelManager = null,
        IModelCatalog? catalog = null)
    {
        var maxContextTokens = GetMaxContextTokens(session.Model, modelManager, catalog);
        var reservedOutput = ResolveOutputReservation(session.Model, modelManager, maxContextTokens);
        // 这里是**告警口径**（完整 transcript 规模），不是模型本次实际收到的输入预算——
        // 后者由 CompactionPipelineBuilder.ResolveInputBudget 计算，非法窗口/输出对会直接抛错。
        // 预留必须与压缩装配走同一条规则：否则本地小窗口下会出现「压缩已按窗口 1/4 预留正常工作、
        // /status 却按固定 8_192 永远 100% 告警」的矛盾。仍保留钳制——窗口被配置成 ≤ 预留时
        // MaxInputTokens 变成 1，UsageRatio 立刻越线并持续告警，配置问题以可见告警暴露，
        // 而不是被算成一个「看起来合理」的预算。
        var effectiveMaxTokens = Math.Max(1, maxContextTokens - reservedOutput);
        var estimatedTokens = EstimateTextTokens(systemPrompt, tokenEstimator);

        foreach (var message in session.Messages)
            estimatedTokens += EstimateMessageTokens(message, tokenEstimator);

        return new TokenBudgetStatus(estimatedTokens, effectiveMaxTokens);
    }

    public static int GetMaxContextTokens(string? model, IModelManager? modelManager = null, IModelCatalog? catalog = null)
    {
        if (modelManager is not null && !string.IsNullOrEmpty(model))
        {
            var info = modelManager.Resolve(model);
            if (info is { ContextWindow: > 0 } mi)
                return mi.ContextWindow;
        }

        return ModelContextDefaults.Resolve(model, catalog);
    }

    /// <summary>
    /// 输出预留：仅本地 Ollama 按窗口比例收敛（与 <c>CompactionStrategyFactory</c> 同一规则），
    /// 其余 provider 保持固定 <see cref="ReservedOutputTokens"/>。
    /// <paramref name="modelManager"/> 缺失时无从判断 provider，按云端规则处理。
    /// </summary>
    private static int ResolveOutputReservation(string? model, IModelManager? modelManager, int contextWindow)
    {
        var isLocalOllama = string.Equals(
            modelManager?.Resolve(model)?.ProviderId,
            CoreConstants.ModelProviders.Ollama,
            StringComparison.OrdinalIgnoreCase);

        return isLocalOllama
            ? ModelContextDefaults.ResolveOutputReservation(contextWindow, ReservedOutputTokens)
            : ReservedOutputTokens;
    }

    private static int EstimateMessageTokens(Message message, ITokenEstimator tokenEstimator) => message switch
    {
        UserMessage user => EstimateTextTokens(user.Content, tokenEstimator) + 12,
        AssistantMessage assistant => assistant.Content.Sum(b => EstimateContentBlockTokens(b, tokenEstimator)) + 16,
        ToolResultMessage tool => EstimateTextTokens(tool.Content, tokenEstimator) + 20,
        SystemMessage system => EstimateTextTokens(system.Content, tokenEstimator) + 12,
        _ => 0,
    };

    private static int EstimateContentBlockTokens(ContentBlock block, ITokenEstimator tokenEstimator) => block switch
    {
        TextBlock text => EstimateTextTokens(text.Text, tokenEstimator) + 4,
        ToolUseBlock toolUse => EstimateTextTokens(toolUse.Name, tokenEstimator) + EstimateTextTokens(toolUse.Input, tokenEstimator) + 24,
        _ => 0,
    };

    private static int EstimateTextTokens(string? text, ITokenEstimator tokenEstimator)
        => tokenEstimator.EstimateTokens(text);
}

public sealed record TokenBudgetStatus(int EstimatedInputTokens, int MaxInputTokens)
{
    public int RemainingTokens => Math.Max(0, MaxInputTokens - EstimatedInputTokens);
    public double UsageRatio => MaxInputTokens == 0 ? 0 : (double)EstimatedInputTokens / MaxInputTokens;
}
