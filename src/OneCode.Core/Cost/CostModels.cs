namespace OneCode.Core.Cost;

public sealed record CostBreakdown(
    decimal InputCost,
    decimal OutputCost,
    decimal CacheReadCost,
    decimal CacheWriteCost,
    decimal TotalCost);

public sealed record ModelPricing(
    decimal InputPerMillion,
    decimal OutputPerMillion,
    decimal CacheReadPerMillion = 0m,
    decimal CacheWritePerMillion = 0m)
{
    public static ModelPricing Zero { get; } = new(0, 0, 0, 0);
}

public sealed record ModelPricingTiered
{
    public ModelPricing Base { get; init; }
    public IReadOnlyList<PricingTier>? Tiers { get; init; }
    public ModelPricing? ExperimentalOver200K { get; init; }

    public ModelPricingTiered(ModelPricing basePricing)
    {
        Base = basePricing;
        Tiers = null;
        ExperimentalOver200K = null;
    }

    public ModelPricingTiered(
        ModelPricing basePricing,
        IReadOnlyList<PricingTier>? tiers = null,
        ModelPricing? experimentalOver200K = null)
    {
        Base = basePricing;
        Tiers = tiers;
        ExperimentalOver200K = experimentalOver200K;
    }

    public static ModelPricingTiered Zero { get; } = new(ModelPricing.Zero);
}

public sealed record PricingTier
{
    public required PricingTierType Type { get; init; }
    public required int Size { get; init; }
    public required ModelPricing Pricing { get; init; }
}

public enum PricingTierType
{
    Context,
}

public sealed record UsageRecord(
    string ModelId,
    int InputTokens,
    int OutputTokens,
    int CacheReadTokens = 0,
    int CacheWriteTokens = 0,
    int ReasoningTokens = 0,
    int ContextTokens = 0);

public sealed record CostUpdate(
    decimal InputCost,
    decimal OutputCost,
    decimal CacheReadCost,
    decimal CacheWriteCost,
    decimal TotalCost,
    decimal CumulativeCost,
    int TotalInputTokens,
    int TotalOutputTokens);

/// <summary>Mutable per-session cost accumulator.</summary>
public sealed class SessionCostInfo
{
    private long _inputTokens;
    private long _outputTokens;
    private long _cacheReadTokens;
    private long _cacheWriteTokens;
    private long _reasoningTokens;
    private readonly object _costLock = new();
    private decimal _totalCostUsd;

    public long TotalInputTokens => _inputTokens;
    public long TotalOutputTokens => _outputTokens;
    public long TotalCacheReadTokens => _cacheReadTokens;
    public long TotalCacheWriteTokens => _cacheWriteTokens;
    public long TotalReasoningTokens => _reasoningTokens;
    public decimal TotalCostUsd { get { lock (_costLock) return _totalCostUsd; } }

    public long TotalAllTokens =>
        _inputTokens + _outputTokens + _cacheWriteTokens;

    public string CostDisplay => $"${TotalCostUsd:F4}";

    public void Record(UsageRecord usage, decimal cost)
    {
        Interlocked.Add(ref _inputTokens, usage.InputTokens);
        Interlocked.Add(ref _outputTokens, usage.OutputTokens);
        Interlocked.Add(ref _cacheReadTokens, usage.CacheReadTokens);
        Interlocked.Add(ref _cacheWriteTokens, usage.CacheWriteTokens);
        Interlocked.Add(ref _reasoningTokens, usage.ReasoningTokens);
        lock (_costLock)
        {
            _totalCostUsd += cost;
        }
    }

    public override string ToString() =>
        $"in:{_inputTokens:N0} out:{_outputTokens:N0} " +
        $"cache:(r:{_cacheReadTokens:N0} w:{_cacheWriteTokens:N0}) " +
        $"reasoning:{_reasoningTokens:N0} {CostDisplay}";
}
