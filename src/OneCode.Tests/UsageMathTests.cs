// UsageMath：预算熔断的"可计费"计量口径——缓存读按 10% 折算，防止长会话
// 的重复上下文计费把预算打穿（见 UsageMath remarks）。
using OneCode.Core.Tokens;

namespace OneCode.Tests;

public sealed class UsageMathTests
{
    [Fact]
    public void BillableTokens_NoCache_FullInputPlusOutput()
    {
        UsageMath.BillableTokens(new UsageRecord("m", InputTokens: 1_000_000, OutputTokens: 500_000))
            .Should().Be(1_500_000);
    }

    [Fact]
    public void BillableTokens_CacheReadCountedAtTenthWeight()
    {
        // InputTokens=1.5M 含 CacheRead=0.5M 子集：非缓存 1M + 输出 1M + 缓存读 50k = 2.05M
        UsageMath.BillableTokens(new UsageRecord("m", InputTokens: 1_500_000, OutputTokens: 1_000_000, CacheReadTokens: 500_000))
            .Should().Be(2_050_000);
    }

    [Fact]
    public void BillableTokens_CacheWriteCountedFully()
    {
        UsageMath.BillableTokens(new UsageRecord("m", InputTokens: 1_000, OutputTokens: 0, CacheWriteTokens: 300))
            .Should().Be(1_300);
    }

    [Fact]
    public void BillableTokens_PureCacheHits_StillAccumulate()
    {
        // 失控循环兜底：纯缓存命中打转（input=cacheRead）仍按 10% 累计，不会永远零计费
        var oneTurn = UsageMath.BillableTokens(new UsageRecord("m", InputTokens: 100_000, OutputTokens: 0, CacheReadTokens: 100_000));
        oneTurn.Should().Be(10_000);
        UsageMath.BillableTokens(new UsageRecord("m", InputTokens: 0, OutputTokens: 0, CacheReadTokens: 0, CacheWriteTokens: 0))
            .Should().Be(0);
    }

    [Fact]
    public void BillableTokens_CacheReadExceedingInput_ClampedToZero()
    {
        // 防御：个别 provider 上报越界（cacheRead > input）不得算出负数
        UsageMath.BillableTokens(new UsageRecord("m", InputTokens: 100, OutputTokens: 0, CacheReadTokens: 1_000))
            .Should().Be(100);
    }
}
