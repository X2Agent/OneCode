using OneCode.App.Services.Observability;
using OneCode.Core.Domain;
using OneCode.Infrastructure.Api;

namespace OneCode.Tests;

public sealed class TokenUsageTrackerCalibrationTests
{
    [Fact]
    public void CalibrationFactor_BelowMinSamples_StaysAtOne()
    {
        var sut = new TokenUsageTracker(new CostTracker(), TestSupport.NullSessionIdProvider.Instance);
        var breakdown = new TokenBreakdown(100, 200, 300, 0, 600);
        sut.Record(new TokenUsage(720, 100), breakdown);

        sut.CalibrationFactor.Should().Be(1.0);
        sut.QueryCount.Should().Be(1);
    }

    [Fact]
    public void CalibrationFactor_AfterTwoSamples_ConvergesToAverage()
    {
        var sut = new TokenUsageTracker(new CostTracker(), TestSupport.NullSessionIdProvider.Instance);
        var breakdown1 = new TokenBreakdown(100, 200, 300, 0, 600);
        var breakdown2 = new TokenBreakdown(110, 220, 330, 0, 660);

        sut.Record(new TokenUsage(720, 100), breakdown1);
        sut.Record(new TokenUsage(792, 110), breakdown2);

        sut.CalibrationFactor.Should().BeApproximately(1.2, 0.001);
    }

    [Fact]
    public void CalibrationFactor_UsesMovingAverageWithMaxSamples()
    {
        var sut = new TokenUsageTracker(new CostTracker(), TestSupport.NullSessionIdProvider.Instance);

        for (int i = 0; i < 10; i++)
        {
            sut.Record(new TokenUsage(1000, 100), new TokenBreakdown(500, 300, 200, 0, 1000));
        }
        sut.CalibrationFactor.Should().BeApproximately(1.0, 0.001);

        for (int i = 0; i < 5; i++)
        {
            sut.Record(new TokenUsage(1500, 100), new TokenBreakdown(500, 300, 200, 0, 1000));
        }
        // 最近 10 次 = 5 个 1.0 + 5 个 1.5，平均 1.25
        sut.CalibrationFactor.Should().BeApproximately(1.25, 0.01);
    }

    [Fact]
    public void CalibrationFactor_Reset_ReturnsToOne()
    {
        var sut = new TokenUsageTracker(new CostTracker(), TestSupport.NullSessionIdProvider.Instance);
        var breakdown = new TokenBreakdown(100, 200, 300, 0, 600);
        sut.Record(new TokenUsage(720, 100), breakdown);
        sut.Record(new TokenUsage(720, 100), breakdown);

        sut.Reset();

        sut.CalibrationFactor.Should().Be(1.0);
        sut.QueryCount.Should().Be(0);
    }

    [Fact]
    public void CalibrationFactor_ZeroEstimate_DoesNotRecord()
    {
        var sut = new TokenUsageTracker(new CostTracker(), TestSupport.NullSessionIdProvider.Instance);
        var breakdown = new TokenBreakdown(0, 0, 0, 0, 0);

        sut.Record(new TokenUsage(100, 50), breakdown);
        sut.Record(new TokenUsage(100, 50), breakdown);

        sut.CalibrationFactor.Should().Be(1.0);
    }

    [Fact]
    public void Record_AnthropicCacheWriteToken_Accumulated()
    {
        var sut = new TokenUsageTracker(new CostTracker(), TestSupport.NullSessionIdProvider.Instance);
        sut.Record(new TokenUsage(1000, 500, CacheReadTokens: 200, CacheWriteTokens: 300));

        sut.TotalCacheWriteTokens.Should().Be(300);
        sut.TotalCacheReadTokens.Should().Be(200);
    }

    [Fact]
    public void CacheHitRate_WithBothInputAndCacheRead_CalculatedCorrectly()
    {
        var sut = new TokenUsageTracker(new CostTracker(), TestSupport.NullSessionIdProvider.Instance);
        // InputTokens=1000 (完整输入，含缓存命中), CacheReadTokens=700 (其中命中部分)
        // 命中率 = 700 / 1000 = 0.7
        sut.Record(new TokenUsage(1000, 100, CacheReadTokens: 700, CacheWriteTokens: 0));

        sut.CacheHitRate.Should().BeApproximately(0.7, 0.001);
    }
}
