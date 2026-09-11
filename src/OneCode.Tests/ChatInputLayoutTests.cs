using OneCode.App.Tui;

namespace OneCode.Tests;

public sealed class ChatInputLayoutTests
{
    [Fact]
    public void Spacing_GapsAreZero_EliminatingWastedRows()
    {
        TuiSpacing.StatusBarTopGap.Should().Be(0);
        TuiSpacing.ChatInputContextGap.Should().Be(0);
    }

    [Fact]
    public void Spacing_ContentZoneReservedBottom_IsFourRowsForSingleLine()
    {
        // 1(SessionBar) + 1(StatusBar) + 0(TopGap) + 0(ContextGap) + 2(MinHeight) = 4
        TuiSpacing.ContentZoneReservedBottom.Should().Be(4);
    }

    [Fact]
    public void ChatInput_HeightBounds_FollowSpecification()
    {
        ChatInputView.MinHeight.Should().Be(2);
        ChatInputView.MaxHeight.Should().Be(6);
    }

    [Theory]
    [InlineData(24, 4)]  // 24 行终端：上限 4 行（1 分隔线 + 3 编辑行），兑现小屏承诺
    [InlineData(40, 6)]  // 40 行终端：可优雅展开至 6 行
    [InlineData(50, 6)]  // 大屏封顶 6 行
    [InlineData(12, 2)]  // 极小屏退化为最小 2 行
    public void GetInputMaxTotalHeight_FollowsSixthOfViewportRule(int screenHeight, int expected)
    {
        TuiSpacing.GetInputMaxTotalHeight(screenHeight).Should().Be(expected);
    }
}
