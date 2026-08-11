using Microsoft.Extensions.AI;

namespace OneCode.App.Services.Observability;

/// <summary>
/// Estimates per-section input token breakdown for observability / status UI.
/// </summary>
public interface ITokenBreakdownEstimator
{
    TokenBreakdown Estimate(
        string? systemPrompt,
        IReadOnlyList<AIFunction>? tools,
        IReadOnlyList<ChatMessage>? messages,
        int? actualInputTokens = null);
}
