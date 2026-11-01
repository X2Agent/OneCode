using OneCode.App.Services.Observability;
using OneCode.Infrastructure.Api;

namespace OneCode.Tests;

public sealed class TokenUsageTrackerTests
{
    [Fact]
    public void Record_AccumulatesTokensAcrossCalls()
    {
        var sut = new TokenUsageTracker(new TokenLedger(), TestSupport.NullSessionIdProvider.Instance);

        // InputTokens 已含 CacheReadTokens 子集（MEAI 契约），所以 InputTokens ≥ CacheReadTokens
        sut.Record(new TokenUsage(300, 50, CacheReadTokens: 200, CacheWriteTokens: 30));
        sut.Record(new TokenUsage(230, 40, CacheReadTokens: 150, CacheWriteTokens: 20));

        sut.TotalInputTokens.Should().Be(530);
        sut.TotalOutputTokens.Should().Be(90);
        sut.TotalCacheReadTokens.Should().Be(350);
        sut.TotalCacheWriteTokens.Should().Be(50);
        sut.QueryCount.Should().Be(2);
    }

    [Fact]
    public void CacheHitRate_CalculatedCorrectly()
    {
        var sut = new TokenUsageTracker(new TokenLedger(), TestSupport.NullSessionIdProvider.Instance);

        // InputTokens=400 (完整输入，含缓存命中), CacheReadTokens=300 (其中命中部分)
        // 命中率 = CacheReadTokens / InputTokens = 300 / 400 = 0.75
        sut.Record(new TokenUsage(400, 0, CacheReadTokens: 300, CacheWriteTokens: 0));

        sut.CacheHitRate.Should().Be(0.75);
    }

    [Fact]
    public void CacheHitRate_NoCache_ReturnsZero()
    {
        var sut = new TokenUsageTracker(new TokenLedger(), TestSupport.NullSessionIdProvider.Instance);

        sut.Record(new TokenUsage(100, 50));

        sut.CacheHitRate.Should().Be(0);
    }

    [Fact]
    public void Record_WithBreakdown_StoresLastBreakdown()
    {
        var sut = new TokenUsageTracker(new TokenLedger(), TestSupport.NullSessionIdProvider.Instance);
        var breakdown1 = new TokenBreakdown(100, 200, 0, 300, new Dictionary<string, int>(), 50, 650);
        var breakdown2 = new TokenBreakdown(110, 220, 0, 330, new Dictionary<string, int>(), 40, 700);

        sut.Record(new TokenUsage(650, 50), breakdown1);
        sut.Record(new TokenUsage(700, 60), breakdown2);

        sut.LastBreakdown.Should().Be(breakdown2);
    }

    [Fact]
    public void Record_NullUsage_DoesNothing()
    {
        var sut = new TokenUsageTracker(new TokenLedger(), TestSupport.NullSessionIdProvider.Instance);

        sut.Record(null);

        sut.TotalInputTokens.Should().Be(0);
        sut.QueryCount.Should().Be(0);
    }

    [Fact]
    public void Reset_ClearsAllCounters()
    {
        var sut = new TokenUsageTracker(new TokenLedger(), TestSupport.NullSessionIdProvider.Instance);
        sut.Record(new TokenUsage(100, 50, CacheReadTokens: 200, CacheWriteTokens: 30));
        sut.Record(new TokenUsage(80, 40, CacheReadTokens: 150, CacheWriteTokens: 20),
            new TokenBreakdown(10, 20, 0, 30, new Dictionary<string, int>(), 0, 60));

        sut.Reset();

        sut.TotalInputTokens.Should().Be(0);
        sut.TotalOutputTokens.Should().Be(0);
        sut.TotalCacheReadTokens.Should().Be(0);
        sut.TotalCacheWriteTokens.Should().Be(0);
        sut.QueryCount.Should().Be(0);
        sut.LastBreakdown.Should().BeNull();
        sut.LastTurnInputTokens.Should().Be(0);
        sut.CumulativeBreakdown.Should().BeNull();
    }

    [Fact]
    public void TotalAllTokens_SumsAllCategories()
    {
        var sut = new TokenUsageTracker(new TokenLedger(), TestSupport.NullSessionIdProvider.Instance);
        // InputTokens=300 已含 CacheReadTokens=200 子集（MEAI 契约），TotalAll 不再加 CacheRead
        sut.Record(new TokenUsage(300, 50, CacheReadTokens: 200, CacheWriteTokens: 30));

        sut.TotalAllTokens.Should().Be(380); // 300+50+30
    }

    [Fact]
    public void Record_LastTurnInputTokens_StoresLatestWatermark()
    {
        var sut = new TokenUsageTracker(new TokenLedger(), TestSupport.NullSessionIdProvider.Instance);

        sut.Record(new TokenUsage(1000, 50), lastTurnInputTokens: 22_000);
        sut.Record(new TokenUsage(800, 40), lastTurnInputTokens: 25_000);

        sut.LastTurnInputTokens.Should().Be(25_000, "占用水位随每次记录覆盖为最新值");
    }

    [Fact]
    public void Record_AccumulatesCumulativeBreakdown_PerComponent()
    {
        var sut = new TokenUsageTracker(new TokenLedger(), TestSupport.NullSessionIdProvider.Instance);

        sut.Record(new TokenUsage(1000, 50), new TokenBreakdown(
            100, 60, 40, 200,
            new Dictionary<string, int> { ["TodoProvider"] = 10, ["ModeProvider"] = 5 },
            0, 415));
        sut.Record(new TokenUsage(1000, 50), new TokenBreakdown(
            110, 70, 50, 300,
            new Dictionary<string, int> { ["TodoProvider"] = 20 },
            10, 560));

        var cumulative = sut.CumulativeBreakdown;
        cumulative.Should().NotBeNull();
        cumulative!.SystemPrompt.Should().Be(210);
        cumulative.ProductTools.Should().Be(130);
        cumulative.McpTools.Should().Be(90);
        cumulative.Messages.Should().Be(500);
        cumulative.Other.Should().Be(10);
        cumulative.ContextProviders["TodoProvider"].Should().Be(30, "同一 sourceId 跨查询累计");
        cumulative.ContextProviders["ModeProvider"].Should().Be(5);
        cumulative.TotalEstimated.Should().Be(210 + 130 + 90 + 500 + 10 + 30 + 5);
    }

    [Fact]
    public void Record_WithoutBreakdown_LeavesCumulativeNull()
    {
        var sut = new TokenUsageTracker(new TokenLedger(), TestSupport.NullSessionIdProvider.Instance);

        sut.Record(new TokenUsage(100, 50));

        sut.CumulativeBreakdown.Should().BeNull("无分场景估算时不产生累计快照");
    }

    [Fact]
    public void Record_NonPositiveWatermark_KeepsPreviousValue()
    {
        var sut = new TokenUsageTracker(new TokenLedger(), TestSupport.NullSessionIdProvider.Instance);

        sut.Record(new TokenUsage(1000, 50), lastTurnInputTokens: 22_000);
        sut.Record(new TokenUsage(800, 40), lastTurnInputTokens: 0);

        sut.LastTurnInputTokens.Should().Be(22_000, "0 哨兵不回退已有水位");
    }

    // Calibration factor (migrated from TokenUsageTrackerCalibrationTests)

    [Fact]
    public void CalibrationFactor_BelowMinSamples_StaysAtOne()
    {
        var sut = new TokenUsageTracker(new TokenLedger(), TestSupport.NullSessionIdProvider.Instance);
        var breakdown = new TokenBreakdown(100, 200, 0, 300, new Dictionary<string, int>(), 0, 600);
        sut.Record(new TokenUsage(720, 100), breakdown);

        sut.CalibrationFactor.Should().Be(1.0);
        sut.QueryCount.Should().Be(1);
    }

    [Fact]
    public void CalibrationFactor_AfterTwoSamples_ConvergesToAverage()
    {
        var sut = new TokenUsageTracker(new TokenLedger(), TestSupport.NullSessionIdProvider.Instance);
        var breakdown1 = new TokenBreakdown(100, 200, 0, 300, new Dictionary<string, int>(), 0, 600);
        var breakdown2 = new TokenBreakdown(110, 220, 0, 330, new Dictionary<string, int>(), 0, 660);

        sut.Record(new TokenUsage(720, 100), breakdown1);
        sut.Record(new TokenUsage(792, 110), breakdown2);

        sut.CalibrationFactor.Should().BeApproximately(1.2, 0.001);
    }

    [Fact]
    public void CalibrationFactor_UsesMovingAverageWithMaxSamples()
    {
        var sut = new TokenUsageTracker(new TokenLedger(), TestSupport.NullSessionIdProvider.Instance);

        for (int i = 0; i < 10; i++)
        {
            sut.Record(new TokenUsage(1000, 100), new TokenBreakdown(500, 300, 0, 200, new Dictionary<string, int>(), 0, 1000));
        }
        sut.CalibrationFactor.Should().BeApproximately(1.0, 0.001);

        for (int i = 0; i < 5; i++)
        {
            sut.Record(new TokenUsage(1500, 100), new TokenBreakdown(500, 300, 0, 200, new Dictionary<string, int>(), 0, 1000));
        }
        // 最近 10 次 = 5 个 1.0 + 5 个 1.5，平均 1.25
        sut.CalibrationFactor.Should().BeApproximately(1.25, 0.01);
    }

    [Fact]
    public void CalibrationFactor_Reset_ReturnsToOne()
    {
        var sut = new TokenUsageTracker(new TokenLedger(), TestSupport.NullSessionIdProvider.Instance);
        var breakdown = new TokenBreakdown(100, 200, 0, 300, new Dictionary<string, int>(), 0, 600);
        sut.Record(new TokenUsage(720, 100), breakdown);
        sut.Record(new TokenUsage(720, 100), breakdown);

        sut.Reset();

        sut.CalibrationFactor.Should().Be(1.0);
        sut.QueryCount.Should().Be(0);
    }

    [Fact]
    public void CalibrationFactor_ZeroEstimate_DoesNotRecord()
    {
        var sut = new TokenUsageTracker(new TokenLedger(), TestSupport.NullSessionIdProvider.Instance);
        var breakdown = new TokenBreakdown(0, 0, 0, 0, new Dictionary<string, int>(), 0, 0);

        sut.Record(new TokenUsage(100, 50), breakdown);
        sut.Record(new TokenUsage(100, 50), breakdown);

        sut.CalibrationFactor.Should().Be(1.0);
    }

    [Fact]
    public void Record_AnthropicCacheWriteToken_Accumulated()
    {
        var sut = new TokenUsageTracker(new TokenLedger(), TestSupport.NullSessionIdProvider.Instance);
        sut.Record(new TokenUsage(1000, 500, CacheReadTokens: 200, CacheWriteTokens: 300));

        sut.TotalCacheWriteTokens.Should().Be(300);
        sut.TotalCacheReadTokens.Should().Be(200);
    }

    [Fact]
    public void CacheHitRate_WithBothInputAndCacheRead_CalculatedCorrectly()
    {
        var sut = new TokenUsageTracker(new TokenLedger(), TestSupport.NullSessionIdProvider.Instance);
        // InputTokens=1000 (完整输入，含缓存命中), CacheReadTokens=700 (其中命中部分)
        // 命中率 = 700 / 1000 = 0.7
        sut.Record(new TokenUsage(1000, 100, CacheReadTokens: 700, CacheWriteTokens: 0));

        sut.CacheHitRate.Should().BeApproximately(0.7, 0.001);
    }
}
