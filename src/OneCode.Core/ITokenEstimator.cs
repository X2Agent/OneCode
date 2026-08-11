namespace OneCode.Core;

/// <summary>Character-class weighted token estimator (approximate; API Usage is authoritative for billing).</summary>
public interface ITokenEstimator
{
    int EstimateTokens(string? text);
    int EstimateTokens(string? text, string? modelId);
    (string text, int tokens) TruncateToBudget(string text, int maxTokens);
}
