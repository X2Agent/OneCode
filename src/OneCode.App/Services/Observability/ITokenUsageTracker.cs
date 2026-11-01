namespace OneCode.App.Services.Observability;

/// <summary>
/// Token 使用量统计接口——供 /status stats 命令读取累积统计和分场景估算。
/// 由 <see cref="TokenUsageTracker"/> 实现，由 QueryStreamEngine 在每次 LLM 调用后更新。
/// </summary>
public interface ITokenUsageTracker
{
    /// <summary>累计输入 token（完整输入，已含缓存命中部分——MEAI 契约下 InputTokens ⊇ CacheReadTokens）。</summary>
    long TotalInputTokens { get; }

    /// <summary>累计输出 token。</summary>
    long TotalOutputTokens { get; }

    /// <summary>累计缓存读 token（命中部分，是 <see cref="TotalInputTokens"/> 的子集）。</summary>
    long TotalCacheReadTokens { get; }

    /// <summary>累计缓存写 token。</summary>
    long TotalCacheWriteTokens { get; }

    /// <summary>累计总量 = Input(已含缓存读) + Output + CacheWrite（原始口径，缓存读不得再加一遍）。</summary>
    long TotalAllTokens { get; }

    /// <summary>累计查询次数（LLM 调用次数）。</summary>
    int QueryCount { get; }

    /// <summary>缓存命中率（0.0-1.0）。</summary>
    double CacheHitRate { get; }

    /// <summary>最近一次分场景估算结果。</summary>
    TokenBreakdown? LastBreakdown { get; }

    /// <summary>
    /// 最近一次模型调用的实际输入 token（当前上下文占用水位）。
    /// 0 = 本进程内尚无模型调用（进程重启后为 0，不跨重启持久化）。
    /// </summary>
    int LastTurnInputTokens { get; }

    /// <summary>
    /// 分项会话累计：各查询估算的逐分量累加（System prompt / Tools / Messages / Other 逐项求和，
    /// ContextProviders 按 sourceId 累计）。无记录时为 null。
    /// </summary>
    TokenBreakdown? CumulativeBreakdown { get; }

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
    /// <param name="lastTurnInputTokens">最近一轮模型调用的实际输入 token（上下文占用水位；0 表示不更新）。</param>
    void Record(TokenUsage? usage, TokenBreakdown? breakdown = null, int lastTurnInputTokens = 0);

    /// <summary>
    /// 重置 fallback 累加器和校准状态。在会话切换时由 SessionManager 调用。
    /// </summary>
    void Reset();
}
