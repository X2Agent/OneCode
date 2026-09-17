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
    public void Spacing_ContentZoneReservedBottom_IsSixRowsForFixedInput()
    {
        // 1(SessionBar) + 1(StatusBar) + 0(TopGap) + 0(ContextGap) + 4(FixedHeight) = 6
        TuiSpacing.ContentZoneReservedBottom.Should().Be(6);
    }

    [Fact]
    public void ChatInput_FixedHeight_FollowsSpecification()
    {
        ChatInputView.EditorLines.Should().Be(3);
        ChatInputView.FixedHeight.Should().Be(4);
    }
}
