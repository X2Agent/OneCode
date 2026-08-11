using OneCode.Core.Domain;

namespace OneCode.Core.Cost;

/// <summary>
/// Session and process-level LLM cost tracking.
/// </summary>
public interface ICostTracker
{
    void RegisterPricing(string modelId, ModelPricingTiered pricing);
    void RegisterPricing(string modelId, ModelPricing basePricing);
    bool HasPricing(string modelId);
    void SyncPricingFromCatalog();
    CostUpdate RecordUsage(UsageRecord record);
    CostUpdate RecordUsage(SessionId sessionId, UsageRecord record);
    SessionCostInfo? GetSessionCost(SessionId sessionId);
    void RemoveSession(SessionId sessionId);
    decimal GetTotalCost();
    string FormatCost(decimal maxDecimalPlaces = 4);
    void Reset();
}
