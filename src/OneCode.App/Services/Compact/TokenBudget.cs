using OneCode.Core;
using OneCode.Core.Models;

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
        var effectiveMaxTokens = Math.Max(1, maxContextTokens - ReservedOutputTokens);
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
