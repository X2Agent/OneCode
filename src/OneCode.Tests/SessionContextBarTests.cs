using OneCode.App.Tui;

namespace OneCode.Tests;

/// <summary>
/// <see cref="SessionContextBar"/> 模式感知守护：
/// TEAM/GOAL 模式下底部栏不显示 ctx 上下文占用百分比（由各模式编排层管理，
/// 单轮快照百分比无参考价值）；BUILD/PLAN 模式保持显示。
/// </summary>
public sealed class SessionContextBarTests
{
    [Theory]
    [InlineData(WorkingMode.Build, true)]
    [InlineData(WorkingMode.Plan, true)]
    [InlineData(WorkingMode.Team, false)]
    [InlineData(WorkingMode.Goal, false)]
    public void ShowsContextGauge_DependsOnWorkingMode(WorkingMode mode, bool expected)
    {
        SessionContextBar.ShowsContextGauge(mode).Should().Be(expected);
    }

    [Theory]
    [InlineData("short-repo", 20, "short-repo")]
    [InlineData("C:\\projects\\OneCode", 20, "OneCode")]
    public void ShortenPath_WithinMaxChars_ReturnsOriginalName(string path, int maxChars, string expected)
    {
        SessionContextBar.ShortenPath(path, maxChars).Should().Be(expected);
    }

    [Fact]
    public void ShortenPath_ExceedsMaxChars_AppliesMiddleEllipsis()
    {
        var longName = "this-is-a-very-long-repository-name-that-exceeds";
        var result = SessionContextBar.ShortenPath(longName, 20);
        result.Length.Should().Be(20);
        result.Should().Contain(TuiGlyphs.Ellipsis);
        result.Should().StartWith("this-is-a");
        result.Should().EndWith("t-exceeds");
    }
}
