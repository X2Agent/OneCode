using OneCode.Core.Cost;
using OneCode.Core.Domain;
using OneCode.Core.Models;

namespace OneCode.Infrastructure.Api;

public sealed class CostTracker : ICostTracker
{
    private readonly object _globalLock = new();
    private decimal _totalCostUsd;

    private readonly ConcurrentDictionary<string, ModelPricingTiered> _pricing =
        new(StringComparer.OrdinalIgnoreCase);

    // 标记由用户通过 RegisterPricing / settings.json 配置的模型，
    // SyncPricingFromCatalog 热刷新时跳过这些模型，保留用户覆盖。
    private readonly ConcurrentDictionary<string, byte> _userConfiguredModels =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly ConcurrentDictionary<SessionId, SessionCostInfo> _sessionCosts =
        new();

    private readonly ILogger<CostTracker>? _logger;
    private readonly IModelCatalog? _modelCatalog;

    public CostTracker(
        ILogger<CostTracker>? logger = null,
        Dictionary<string, ModelPricingTiered>? configuredPricing = null,
        IModelCatalog? modelCatalog = null)
    {
        _logger = logger;
        _modelCatalog = modelCatalog;

        if (configuredPricing is not null)
        {
            foreach (var entry in configuredPricing)
                RegisterPricing(entry.Key, entry.Value);
        }
    }

    public void RegisterPricing(string modelId, ModelPricingTiered pricing)
    {
        _pricing[modelId] = pricing;
        _userConfiguredModels.TryAdd(modelId, 0);
    }

    public void RegisterPricing(string modelId, ModelPricing basePricing)
    {
        _pricing[modelId] = new ModelPricingTiered(basePricing);
        _userConfiguredModels.TryAdd(modelId, 0);
    }

    /// <inheritdoc />
    public bool HasPricing(string modelId) => _pricing.ContainsKey(modelId);

    /// <inheritdoc />
    public void SyncPricingFromCatalog()
    {
        var catalog = _modelCatalog;
        if (catalog is null)
            return;

        foreach (var (modelId, costInfo) in catalog.GetAllCosts())
        {
            if (_userConfiguredModels.ContainsKey(modelId))
                continue;

            var pricing = new ModelPricing(
                costInfo.InputPerMillion,
                costInfo.OutputPerMillion,
                costInfo.CacheReadPerMillion,
                costInfo.CacheWritePerMillion);
            _pricing[modelId] = new ModelPricingTiered(pricing);
        }
    }

    public CostUpdate RecordUsage(UsageRecord record)
    {
        var pricing = _pricing.GetValueOrDefault(record.ModelId);
        if (pricing == null)
        {
            _logger?.LogWarning("No pricing configured for model {ModelId}, using zero cost", record.ModelId);
            pricing = ModelPricingTiered.Zero;
        }

        var applicablePricing = ResolveApplicablePricing(pricing, record.ContextTokens);
        var cost = CalculateCost(record, applicablePricing);

        lock (_globalLock)
        {
            _totalCostUsd += cost.TotalCost;
        }

        return new CostUpdate(
            InputCost: cost.InputCost,
            OutputCost: cost.OutputCost,
            CacheReadCost: cost.CacheReadCost,
            CacheWriteCost: cost.CacheWriteCost,
            TotalCost: cost.TotalCost,
            CumulativeCost: _totalCostUsd,
            TotalInputTokens: record.InputTokens,
            TotalOutputTokens: record.OutputTokens);
    }

    public CostUpdate RecordUsage(SessionId sessionId, UsageRecord record)
    {
        var update = RecordUsage(record);

        _sessionCosts.AddOrUpdate(
            sessionId,
            _ =>
            {
                var info = new SessionCostInfo();
                info.Record(record, update.TotalCost);
                return info;
            },
            (_, existing) =>
            {
                existing.Record(record, update.TotalCost);
                return existing;
            });

        return update;
    }

    public SessionCostInfo? GetSessionCost(SessionId sessionId) =>
        _sessionCosts.TryGetValue(sessionId, out var info) ? info : null;

    public void RemoveSession(SessionId sessionId) =>
        _sessionCosts.TryRemove(sessionId, out _);

    public decimal GetTotalCost()
    {
        lock (_globalLock)
            return _totalCostUsd;
    }

    public string FormatCost(decimal maxDecimalPlaces = 4)
    {
        var cost = GetTotalCost();
        if (cost < 0.0001m) return "$0.00";
        var decimals = Math.Clamp((int)maxDecimalPlaces, 0, 4);
        cost = Math.Round(cost, decimals);
        return $"${cost.ToString($"F{decimals}", CultureInfo.InvariantCulture)}";
    }

    public void Reset()
    {
        lock (_globalLock)
        {
            _totalCostUsd = 0;
            _sessionCosts.Clear();
        }
    }

    private static ModelPricing ResolveApplicablePricing(ModelPricingTiered pricing, int contextTokens)
    {
        if (pricing.ExperimentalOver200K is not null && contextTokens > 200_000)
            return pricing.ExperimentalOver200K;

        if (pricing.Tiers is { Count: > 0 })
        {
            var matchingTier = pricing.Tiers
                .Where(t => t.Type == PricingTierType.Context && contextTokens > t.Size)
                .OrderByDescending(t => t.Size)
                .FirstOrDefault();

            if (matchingTier is not null)
                return matchingTier.Pricing;
        }

        return pricing.Base;
    }

    private static CostBreakdown CalculateCost(UsageRecord record, ModelPricing pricing)
    {
        var nonCachedInputTokens = Math.Max(0, record.InputTokens - record.CacheReadTokens);
        var inputCost = (decimal)nonCachedInputTokens / 1_000_000m * pricing.InputPerMillion;
        var outputCost = (decimal)record.OutputTokens / 1_000_000m * pricing.OutputPerMillion;
        var cacheReadCost = (decimal)record.CacheReadTokens / 1_000_000m * pricing.CacheReadPerMillion;
        var cacheWriteCost = (decimal)record.CacheWriteTokens / 1_000_000m * pricing.CacheWritePerMillion;
        var totalCost = inputCost + outputCost + cacheReadCost + cacheWriteCost;

        return new CostBreakdown(inputCost, outputCost, cacheReadCost, cacheWriteCost, totalCost);
    }
}
