namespace OneCode.Core.Tokens;

/// <summary>
/// 预算熔断的"可计费"计量口径 — 预算检查的唯一算式。
/// </summary>
/// <remarks>
/// MEAI 契约下 <see cref="UsageRecord.InputTokens"/> 已包含缓存命中子集
/// （<see cref="UsageRecord.CacheReadTokens"/>），而缓存读是对同一段上下文的重复计费
/// （真实价格约为非缓存输入的 10%）。若按 Input + Output 全额累计，长会话每轮请求
/// 都会把整个上下文重算一遍，正常使用必然误触预算熔断。故预算只按真实增量计量，
/// 缓存读按 10% 折算——既贴近真实成本，又保留对失控循环（纯缓存命中打转）的检测能力。
/// </remarks>
public static class UsageMath
{
    /// <summary>缓存读的计费权重（约等于主流 provider 的缓存读折扣价）。</summary>
    public const double CacheReadWeight = 0.1;

    /// <summary>
    /// 可计费 token 数 = (非缓存输入) + 输出 + 缓存写入 + 缓存读×0.1。
    /// </summary>
    public static long BillableTokens(long inputTokens, long outputTokens, long cacheReadTokens, long cacheWriteTokens)
    {
        // 缓存读是输入的子集（MEAI 契约）；防御性截断，个别 provider 上报越界时不得算出负数。
        var nonCachedInput = Math.Max(0, inputTokens - cacheReadTokens);
        return nonCachedInput + outputTokens + cacheWriteTokens
            + (long)(cacheReadTokens * CacheReadWeight);
    }

    /// <summary><see cref="BillableTokens(long, long, long, long)"/> 的记录重载。</summary>
    public static long BillableTokens(UsageRecord record)
        => BillableTokens(record.InputTokens, record.OutputTokens, record.CacheReadTokens, record.CacheWriteTokens);
}
