using OneCode.Core.Cost;
using OneCode.Infrastructure.Api;

namespace OneCode.Tests;

public sealed class CostTrackerTests
{
    private static readonly ModelPricing SonnetPricing = new(3m, 15m, 1.5m, 0m);

    private static CostTracker CreateTrackerWithSonnetPricing()
    {
        return new CostTracker(configuredPricing: new Dictionary<string, ModelPricingTiered>
        {
            ["claude-sonnet-4-6"] = new ModelPricingTiered(SonnetPricing)
        });
    }

    [Fact]
    public void RecordUsage_CalculatesCostCorrectly()
    {
        var tracker = CreateTrackerWithSonnetPricing();

        // InputTokens=2M 已含 CacheReadTokens=1M 子集（MEAI 契约）
        // 非缓存部分 = 2M - 1M = 1M → InputCost = 1M * 3/M = 3.00
        var update = tracker.RecordUsage(new UsageRecord(
            "claude-sonnet-4-6",
            InputTokens: 2_000_000,
            OutputTokens: 1_000_000,
            CacheReadTokens: 1_000_000,
            CacheWriteTokens: 1_000_000));

        update.InputCost.Should().Be(3.00m);
        update.OutputCost.Should().Be(15.00m);
        update.CacheReadCost.Should().Be(1.50m);
        update.CacheWriteCost.Should().Be(0m);
        update.TotalCost.Should().Be(19.50m);
        update.CumulativeCost.Should().Be(update.TotalCost);
    }

    [Fact]
    public void RecordUsage_AccumulatesAcrossCalls()
    {
        var tracker = CreateTrackerWithSonnetPricing();

        tracker.RecordUsage(new UsageRecord("claude-sonnet-4-6", 1_000_000, 0));
        var update2 = tracker.RecordUsage(new UsageRecord("claude-sonnet-4-6", 1_000_000, 0));

        update2.TotalCost.Should().Be(3.00m);
        update2.CumulativeCost.Should().Be(6.00m);
    }

    [Fact]
    public void RecordUsage_UnknownModel_ReturnsZeroCost()
    {
        var tracker = new CostTracker();

        var update = tracker.RecordUsage(new UsageRecord("unknown-model", 1_000_000, 0));

        update.InputCost.Should().Be(0m);
        update.TotalCost.Should().Be(0m);
    }

    [Fact]
    public void GetTotalCost_ReturnsAccumulatedCost()
    {
        var tracker = CreateTrackerWithSonnetPricing();

        tracker.RecordUsage(new UsageRecord("claude-sonnet-4-6", 1_000_000, 0));
        tracker.RecordUsage(new UsageRecord("claude-sonnet-4-6", 1_000_000, 0));

        var total = tracker.GetTotalCost();
        total.Should().Be(6.00m);
    }

    [Fact]
    public void Reset_ClearsAllState()
    {
        var tracker = new CostTracker();

        tracker.RecordUsage(new UsageRecord("claude-sonnet-4-6", 1_000_000, 0));
        tracker.Reset();

        tracker.GetTotalCost().Should().Be(0m);
    }

    [Fact]
    public void FormatCost_FormatsSmallAmounts()
    {
        var tracker = new CostTracker();
        tracker.RecordUsage(new UsageRecord("claude-sonnet-4-6", 1000, 500));

        var formatted = tracker.FormatCost();
        formatted.Should().Be("$0.00");
    }

    [Fact]
    public void RecordUsage_CacheWriteCostCalculated()
    {
        var tracker = new CostTracker();
        tracker.RegisterPricing("test-model", new ModelPricing(1m, 2m, 0.5m, 3m));

        // InputTokens=2M 已含 CacheReadTokens=1M 子集；非缓存 = 1M → InputCost = 1
        var update = tracker.RecordUsage(new UsageRecord("test-model", 2_000_000, 1_000_000, 1_000_000, 1_000_000));

        update.InputCost.Should().Be(1m);
        update.OutputCost.Should().Be(2m);
        update.CacheReadCost.Should().Be(0.5m);
        update.CacheWriteCost.Should().Be(3m);
        update.TotalCost.Should().Be(6.5m);
    }

    [Fact]
    public void RecordUsage_WithSessionId_TracksPerSessionCost()
    {
        var tracker = new CostTracker();
        tracker.RegisterPricing("claude-sonnet-4-6", new ModelPricing(3m, 15m, 1.5m, 0m));

        var sessionId = "session-001";

        tracker.RecordUsage(sessionId, new UsageRecord("claude-sonnet-4-6", 1_000_000, 100_000));
        tracker.RecordUsage(sessionId, new UsageRecord("claude-sonnet-4-6", 500_000, 50_000));

        var sessionCost = tracker.GetSessionCost(sessionId);
        sessionCost.Should().NotBeNull();
        sessionCost!.TotalInputTokens.Should().Be(1_500_000);
        sessionCost!.TotalOutputTokens.Should().Be(150_000);
        sessionCost!.TotalCostUsd.Should().BeApproximately(6.75m, 0.01m);
    }

    [Fact]
    public void RecordUsage_MultipleSessions_TracksIndependently()
    {
        var tracker = new CostTracker();
        tracker.RegisterPricing("claude-sonnet-4-6", new ModelPricing(3m, 15m, 0m, 0m));

        tracker.RecordUsage("session-A", new UsageRecord("claude-sonnet-4-6", 1_000_000, 0));
        tracker.RecordUsage("session-B", new UsageRecord("claude-sonnet-4-6", 100_000, 0));

        var costA = tracker.GetSessionCost("session-A");
        var costB = tracker.GetSessionCost("session-B");

        costA!.TotalInputTokens.Should().Be(1_000_000);
        costB!.TotalInputTokens.Should().Be(100_000);
        costA.TotalCostUsd.Should().BeGreaterThan(costB!.TotalCostUsd);
    }

    [Fact]
    public void RemoveSession_CleansUpSessionData()
    {
        var tracker = new CostTracker();

        tracker.RecordUsage("to-remove", new UsageRecord("claude-sonnet-4-6", 1_000_000, 0));
        tracker.RemoveSession("to-remove");

        tracker.GetSessionCost("to-remove").Should().BeNull();
        // global cost should still be accumulated
        tracker.GetTotalCost().Should().Be(0m);
    }

    [Fact]
    public void RecordUsage_TieredPricing_SelectsCorrectTier()
    {
        // Pricing: base=$2/$10, context over 128k uses $2.5/$15, over 200k uses experimental $5/$25
        var tieredPricing = new ModelPricingTiered(
            new ModelPricing(2m, 10m),
            tiers: new List<PricingTier>
            {
                new() { Type = PricingTierType.Context, Size = 128_000, Pricing = new ModelPricing(2.5m, 15m) },
                new() { Type = PricingTierType.Context, Size = 64_000, Pricing = new ModelPricing(2.2m, 12m) },
            },
            experimentalOver200K: new ModelPricing(5m, 25m));

        var tracker = new CostTracker();
        tracker.RegisterPricing("tiered-model", tieredPricing);

        // context=50K → no tier matches, use base
        var update1 = tracker.RecordUsage(new UsageRecord("tiered-model", 1_000_000, 1_000_000, ContextTokens: 50_000));
        update1.InputCost.Should().Be(2m);
        update1.OutputCost.Should().Be(10m);

        // context=100K → 64K tier wins (100K > 64K, but 100K < 128K so only one matches)
        var update2 = tracker.RecordUsage(new UsageRecord("tiered-model", 1_000_000, 1_000_000, ContextTokens: 100_000));
        update2.InputCost.Should().Be(2.2m);
        update2.OutputCost.Should().Be(12m);

        // context=150K → both 64K and 128K match, 128K wins (highest)
        var update2b = tracker.RecordUsage(new UsageRecord("tiered-model", 1_000_000, 1_000_000, ContextTokens: 150_000));
        update2b.InputCost.Should().Be(2.5m);
        update2b.OutputCost.Should().Be(15m);

        // context=250K → experimental over 200K pricing
        var update3 = tracker.RecordUsage(new UsageRecord("tiered-model", 1_000_000, 1_000_000, ContextTokens: 250_000));
        update3.InputCost.Should().Be(5m);
        update3.OutputCost.Should().Be(25m);
    }

    [Fact]
    public void SessionCostInfo_Record_AccumulatesAllTokenTypes()
    {
        var info = new SessionCostInfo();

        info.Record(new UsageRecord("m", 1000, 2000, 0, 0, 500, 3000), 0.05m);
        info.Record(new UsageRecord("m", 100, 200, 0, 0, 50, 0), 0.01m);

        info.TotalInputTokens.Should().Be(1100);
        info.TotalOutputTokens.Should().Be(2200);
        info.TotalCostUsd.Should().Be(0.06m);
    }

    // SessionCostInfo.TotalCostUsd thread safety

    [Fact]
    public void SessionCostInfo_TotalCostUsd_IsReadableViaProperty()
    {
        var info = new SessionCostInfo();
        info.Record(new UsageRecord("m", 100, 50, 0, 0), 1.23m);

        info.TotalCostUsd.Should().Be(1.23m);
    }

    // SyncPricingFromCatalog hot-reload

    [Fact]
    public void HasPricing_ReturnsTrueForRegisteredModel()
    {
        var tracker = new CostTracker();
        tracker.RegisterPricing("my-model", new ModelPricing(1m, 2m));

        tracker.HasPricing("my-model").Should().BeTrue();
        tracker.HasPricing("unknown").Should().BeFalse();
    }

    // FormatCost rounding vs display consistency

    [Fact]
    public void FormatCost_WithTwoDecimalPlaces_DisplaysTwoDecimals()
    {
        var tracker = new CostTracker();
        tracker.RegisterPricing("m", new ModelPricing(3m, 15m));
        // 1M input * 3/M = 3.00
        tracker.RecordUsage(new UsageRecord("m", 1_000_000, 0));

        var formatted = tracker.FormatCost(maxDecimalPlaces: 2);
        formatted.Should().Be("$3.00");
    }

    [Fact]
    public void FormatCost_DefaultFourDecimals()
    {
        var tracker = new CostTracker();
        tracker.RegisterPricing("m", new ModelPricing(1m, 1m));
        // 1234 input * 1/M = 0.001234
        tracker.RecordUsage(new UsageRecord("m", 1234, 0));

        var formatted = tracker.FormatCost();
        formatted.Should().Be("$0.0012");
    }
}
