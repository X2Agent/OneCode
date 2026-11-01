namespace OneCode.Core;

/// <summary>Character-class weighted token estimator (approximate; API Usage is authoritative for billing).</summary>
public interface ITokenEstimator
{
    int EstimateTokens(string? text);
    int EstimateTokens(string? text, string? modelId);

    /// <summary>
    /// 估算字符区间的 token 数，不物化子串。热路径上对同一份文本分段估算时用它，
    /// 避免为每个分段分配一个字符串。
    /// </summary>
    int EstimateTokens(ReadOnlySpan<char> text);

    (string text, int tokens) TruncateToBudget(string text, int maxTokens);
}
