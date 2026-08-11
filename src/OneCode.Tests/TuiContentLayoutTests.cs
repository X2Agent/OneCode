using OneCode.App.Tui;

namespace OneCode.Tests;

public sealed class TuiContentLayoutTests
{
    [Theory]
    [InlineData(80, 80)]
    [InlineData(100, 100)]
    [InlineData(160, 160)]
    [InlineData(200, 200)]
    [InlineData(0, TuiSpacing.DefaultContentWidth)]
    public void GetContentColumnWidth_TracksViewport(int viewportWidth, int expected)
    {
        TuiSpacing.GetContentColumnWidth(viewportWidth).Should().Be(expected);
    }

    [Fact]
    public void ContentZoneReservedBottom_IncludesFullChatInputHeight()
    {
        ChatInputView.MaxHeight.Should().Be(1 + ChatTextEditor.MaxVisibleLines);
        TuiSpacing.ContentZoneReservedBottom.Should().Be(
            TuiSpacing.SessionContextBarHeight
            + TuiSpacing.StatusBarHeight
            + TuiSpacing.StatusBarTopGap
            + TuiSpacing.ChatInputContextGap
            + ChatInputView.MaxHeight);
        TuiSpacing.ContentZoneReservedBottom.Should().Be(9);
    }

    [Fact]
    public void WelcomeRenderer_CentersLogoWithinContentColumn()
    {
        const int width = 100;
        var lines = WelcomeRenderer.Render(new WelcomeInfo("1.0.0"), width);

        var logoLine = lines.Select(l => l.FullText).First(t => t.Contains('█', StringComparison.Ordinal));
        var pad = logoLine.TakeWhile(c => c == ' ').Count();
        var logoWidth = TextWidthHelper.GetDisplayWidth(logoLine.TrimStart());

        pad.Should().Be((width - logoWidth) / 2);
        (pad + logoWidth).Should().BeLessThanOrEqualTo(width);
    }

    [Fact]
    public void WelcomeRenderer_WideColumn_StaysCenteredNotLeftGlued()
    {
        const int width = 160;
        var lines = WelcomeRenderer.Render(new WelcomeInfo("1.0.0"), width);
        var logoLine = lines.Select(l => l.FullText).First(t => t.Contains('█', StringComparison.Ordinal));
        var pad = logoLine.TakeWhile(c => c == ' ').Count();

        pad.Should().BeGreaterThan(width / 4, "logo should not cling to the left of a wide column");
        pad.Should().BeLessThan(width / 2);
    }
}
