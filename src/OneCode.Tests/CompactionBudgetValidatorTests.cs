using Microsoft.Extensions.AI;
using NSubstitute;
using OneCode.Infrastructure.Agent;

namespace OneCode.Tests;

/// <summary>
/// 行为契约：非法模型配置必须显式失败，不能静默退化成不可达的阈值。
/// </summary>
/// <remarks>
/// <para>
/// 输出预留大于等于上下文窗口时，若把预算钳制为 1，三层阈值会随之变成 0——压缩看起来
/// 「已装配」，但任何策略都不可能触发，配置错误被完全掩盖。因此装配阶段必须直接拒绝。
/// </para>
/// <para>
/// 断言打在<b>公开入口</b>（<see cref="CompactionPipelineBuilder"/>）而非内部辅助方法上：
/// 校验逻辑是装配的一部分，测试只认「装配失败」这一可观察结果，不绑定实现位置。
/// </para>
/// </remarks>
public sealed class CompactionBudgetValidatorTests
{
    [Fact]
    public void BuildForMainAgent_OutputExceedsWindow_Throws()
    {
        var act = () => CompactionPipelineBuilder.BuildForMainAgent(
            Substitute.For<IChatClient>(), maxContextWindowTokens: 8192, maxOutputTokens: 8192);

        act.Should().Throw<ArgumentOutOfRangeException>()
            .WithMessage("*no input budget*");
    }

    /// <summary>反证：窗口合法但预留为负（用户配置错误）同样必须失败。</summary>
    [Fact]
    public void BuildForMainAgent_NonPositiveValues_Throw()
    {
        var client = Substitute.For<IChatClient>();

        ((Action)(() => CompactionPipelineBuilder.BuildForMainAgent(client, 0, 1024)))
            .Should().Throw<ArgumentOutOfRangeException>();
        ((Action)(() => CompactionPipelineBuilder.BuildForMainAgent(client, 200_000, -1)))
            .Should().Throw<ArgumentOutOfRangeException>();
    }

    /// <summary>
    /// 反证（防过度拒绝）：上面的负向用例只断言「必须抛」，若校验比较被写反、
    /// 合法配置也被拒，那组用例反而全绿——本用例守卫这一面。
    /// 请求开销预留在内部阈值推导里完成，无公开可观察面，故断言装配成功本身。
    /// </summary>
    [Fact]
    public void BuildForMainAgent_ValidPair_DoesNotThrow()
    {
        var act = () => CompactionPipelineBuilder.BuildForMainAgent(
            Substitute.For<IChatClient>(), 200_000, 8192);

        act.Should().NotThrow("a valid pair must build; rejecting it would silently disable compaction");
    }

    /// <summary>
    /// 反证（本地小窗口）：窗口 4K 时按窗口 1/4 收敛预留得到的 (4096, 1024) 是合法对，
    /// 必须能装配成功——修复前本地模型窗口为 0，装配必然失败。
    /// </summary>
    [Fact]
    public void BuildForMainAgent_SmallWindowQuarterReservation_DoesNotThrow()
    {
        var act = () => CompactionPipelineBuilder.BuildForMainAgent(
            Substitute.For<IChatClient>(), 4_096, 1_024);

        act.Should().NotThrow("a small local window with a proportional reservation must still build");
    }

    /// <summary>
    /// 反证：输出预留恰好等于或超过窗口时必须失败，而不是产生 0 阈值
    /// （0 阈值会让压缩永不触发，且没有任何报错）。
    /// </summary>
    [Theory]
    [InlineData(1000, 1000)]
    [InlineData(1000, 1500)]
    public void BuildForMainAgent_NoRoomForInput_Throws(int window, int output)
    {
        var act = () => CompactionPipelineBuilder.BuildForMainAgent(
            Substitute.For<IChatClient>(), window, output);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
