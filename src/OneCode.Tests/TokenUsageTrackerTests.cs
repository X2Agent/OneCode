using OneCode.Core.Domain;
using OneCode.App.Services.Observability;
using OneCode.Infrastructure.Api;

namespace OneCode.Tests;

public sealed class TokenUsageTrackerTests
{
    [Fact]
    public void Record_AccumulatesTokensAcrossCalls()
    {
        var sut = new TokenUsageTracker(new CostTracker(), TestSupport.NullSessionIdProvider.Instance);

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
        var sut = new TokenUsageTracker(new CostTracker(), TestSupport.NullSessionIdProvider.Instance);

        // InputTokens=400 (完整输入，含缓存命中), CacheReadTokens=300 (其中命中部分)
        // 命中率 = CacheReadTokens / InputTokens = 300 / 400 = 0.75
        sut.Record(new TokenUsage(400, 0, CacheReadTokens: 300, CacheWriteTokens: 0));

        sut.CacheHitRate.Should().Be(0.75);
    }

    [Fact]
    public void CacheHitRate_NoCache_ReturnsZero()
    {
        var sut = new TokenUsageTracker(new CostTracker(), TestSupport.NullSessionIdProvider.Instance);

        sut.Record(new TokenUsage(100, 50));

        sut.CacheHitRate.Should().Be(0);
    }

    [Fact]
    public void Record_WithBreakdown_StoresLastBreakdown()
    {
        var sut = new TokenUsageTracker(new CostTracker(), TestSupport.NullSessionIdProvider.Instance);
        var breakdown1 = new TokenBreakdown(100, 200, 300, 50, 650);
        var breakdown2 = new TokenBreakdown(110, 220, 330, 40, 700);

        sut.Record(new TokenUsage(650, 50), breakdown1);
        sut.Record(new TokenUsage(700, 60), breakdown2);

        sut.LastBreakdown.Should().Be(breakdown2);
    }

    [Fact]
    public void Record_NullUsage_DoesNothing()
    {
        var sut = new TokenUsageTracker(new CostTracker(), TestSupport.NullSessionIdProvider.Instance);

        sut.Record(null);

        sut.TotalInputTokens.Should().Be(0);
        sut.QueryCount.Should().Be(0);
    }

    [Fact]
    public void Reset_ClearsAllCounters()
    {
        var sut = new TokenUsageTracker(new CostTracker(), TestSupport.NullSessionIdProvider.Instance);
        sut.Record(new TokenUsage(100, 50, CacheReadTokens: 200, CacheWriteTokens: 30));
        sut.Record(new TokenUsage(80, 40, CacheReadTokens: 150, CacheWriteTokens: 20),
            new TokenBreakdown(10, 20, 30, 0, 60));

        sut.Reset();

        sut.TotalInputTokens.Should().Be(0);
        sut.TotalOutputTokens.Should().Be(0);
        sut.TotalCacheReadTokens.Should().Be(0);
        sut.TotalCacheWriteTokens.Should().Be(0);
        sut.QueryCount.Should().Be(0);
        sut.LastBreakdown.Should().BeNull();
    }

    [Fact]
    public void TotalAllTokens_SumsAllCategories()
    {
        var sut = new TokenUsageTracker(new CostTracker(), TestSupport.NullSessionIdProvider.Instance);
        // InputTokens=300 已含 CacheReadTokens=200 子集（MEAI 契约），TotalAll 不再加 CacheRead
        sut.Record(new TokenUsage(300, 50, CacheReadTokens: 200, CacheWriteTokens: 30));

        sut.TotalAllTokens.Should().Be(380); // 300+50+30
    }
}
