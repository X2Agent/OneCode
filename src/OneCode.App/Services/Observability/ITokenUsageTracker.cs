namespace OneCode.App.Services.Observability;

/// <summary>
/// Token 使用量统计接口——供 /status stats 命令读取累积统计和分场景估算。
/// 由 <see cref="TokenUsageTracker"/> 实现，由 ChatService 在每次 LLM 调用后更新。
/// </summary>
public interface ITokenUsageTracker
{
    /// <summary>累计输入 token（非缓存部分）。</summary>
    long TotalInputTokens { get; }

    /// <summary>累计输出 token。</summary>
    long TotalOutputTokens { get; }

    /// <summary>累计缓存读 token（命中部分）。</summary>
    long TotalCacheReadTokens { get; }

    /// <summary>累计缓存写 token。</summary>
    long TotalCacheWriteTokens { get; }

    /// <summary>累计查询次数（LLM 调用次数）。</summary>
    int QueryCount { get; }

    /// <summary>缓存命中率（0.0-1.0）。</summary>
    double CacheHitRate { get; }

    /// <summary>最近一次分场景估算结果。</summary>
    TokenBreakdown? LastBreakdown { get; }

    /// <summary>
    /// 当前校准系数（默认 1.0，经过多次调用后收敛到稳定值）。
    /// TokenBreakdownEstimator 用此系数修正估算：估算值 × 校准系数 ≈ API真实值。
    /// </summary>
    double CalibrationFactor { get; }

    /// <summary>
    /// 记录一次 LLM 调用的 token 使用情况，并更新校准系数。
    /// </summary>
    /// <param name="usage">API 返回的 TokenUsage（真实值）。</param>
    /// <param name="breakdown">本次调用的分场景估算（可选，用于校准）。</param>
    void Record(TokenUsage? usage, TokenBreakdown? breakdown = null);

    /// <summary>
    /// 重置 fallback 累加器和校准状态。在会话切换时由 SessionManager 调用。
    /// </summary>
    void Reset();
}
