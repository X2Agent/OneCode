using OneCode.Core.Domain;

namespace OneCode.Core.Tokens;

/// <summary>
/// 单次 LLM 调用的 token 用量记录（MEAI UsageDetails 契约）：
/// - InputTokens：完整输入 token 数（已含缓存命中部分）
/// - OutputTokens：输出 token 数
/// - CacheReadTokens：输入中缓存命中的子集
/// - CacheWriteTokens：缓存写入部分（Anthropic 创生）
/// </summary>
public sealed record UsageRecord(
    string ModelId,
    int InputTokens,
    int OutputTokens,
    int CacheReadTokens = 0,
    int CacheWriteTokens = 0);

/// <summary>按会话累计的 token 用量（可变累加器，线程安全）。</summary>
public sealed class SessionTokenUsage
{
    private long _inputTokens;
    private long _outputTokens;
    private long _cacheReadTokens;
    private long _cacheWriteTokens;

    public long TotalInputTokens => _inputTokens;
    public long TotalOutputTokens => _outputTokens;
    public long TotalCacheReadTokens => _cacheReadTokens;
    public long TotalCacheWriteTokens => _cacheWriteTokens;

    /// <summary>输入 + 输出 + 缓存写入（不含缓存读取，读取是命中不产生新 token）。</summary>
    public long TotalAllTokens =>
        _inputTokens + _outputTokens + _cacheWriteTokens;

    public void Record(UsageRecord usage)
    {
        Interlocked.Add(ref _inputTokens, usage.InputTokens);
        Interlocked.Add(ref _outputTokens, usage.OutputTokens);
        Interlocked.Add(ref _cacheReadTokens, usage.CacheReadTokens);
        Interlocked.Add(ref _cacheWriteTokens, usage.CacheWriteTokens);
    }
}

/// <summary>
/// 会话与进程级 LLM token 用量账本。token 预算（BudgetGuard / GOAL 预算）与
/// 用量统计（TokenUsageTracker）的唯一事实源，避免双账本。
/// </summary>
public interface ITokenLedger
{
    void RecordUsage(UsageRecord record);

    void RecordUsage(SessionId sessionId, UsageRecord record);

    SessionTokenUsage? GetSessionUsage(SessionId sessionId);

    void RemoveSession(SessionId sessionId);

    /// <summary>进程级累计 token 数（输入 + 输出），供预算熔断检查。</summary>
    long GetTotalTokens();
}
