using OneCode.Tui;

namespace OneCode.Tests;

/// <summary>
/// <see cref="SessionContextBar"/> 模式感知守护：
/// GOAL 模式下底部栏不显示 ctx 上下文占用百分比（由编排层管理，
/// 单轮快照百分比无参考价值）；BUILD/PLAN 模式保持显示。
/// </summary>
public sealed class SessionContextBarTests
{
    [Theory]
    [InlineData(WorkingMode.Build, true)]
    [InlineData(WorkingMode.Plan, true)]
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

    /// <summary>
    /// 占用超过窗口（历史口径下累计输入必然越界）时，条形必须钳制在固定格数内，
    /// 否则会画出几十个块把右侧百分比顶出视口。
    /// </summary>
    [Fact]
    public void ContextGauge_OverCapacity_ClampsBarWidth()
    {
        var bar = new SessionContextBar();
        bar.SetContextUsage(maxTokens: 512_000, currentTokens: 5_000_000);

        var segments = bar.BuildRightSegmentsForTest(200);
        var barText = string.Concat(segments.Select(s => s.Text));

        var filled = barText.Count(ch => ch == TuiGlyphs.BlockFull[0]);
        filled.Should().Be(10, "进度条宽度固定 10 格，超出部分不扩大");
        barText.Should().Contain("100%");
        TextWidthHelper.GetDisplayWidth(barText).Should().BeLessThanOrEqualTo(198);
    }

    /// <summary>窄视口下按优先级整组丢弃，最低优先级的「轮次」先消失。</summary>
    [Fact]
    public void ContextGauge_NarrowViewport_DropsLowestPrioritySegments()
    {
        var bar = new SessionContextBar();
        bar.SetTurn(12);
        bar.SetTokens(1_200_000, 34_000);
        bar.SetContextUsage(maxTokens: 512_000, currentTokens: 300_000);

        var wide = string.Concat(bar.BuildRightSegmentsForTest(120).Select(s => s.Text));
        wide.Should().Contain("轮次 12");

        // 宽度刚好只放得下进度条本身。
        var narrow = string.Concat(bar.BuildRightSegmentsForTest(40).Select(s => s.Text));
        narrow.Should().NotContain("轮次", "窄视口先丢最低优先级的段");
        narrow.Should().NotContain("↓", "再丢 token 段");
        narrow.Should().Contain("ctx", "上下文进度条优先级最高，必须保留");
        TextWidthHelper.GetDisplayWidth(narrow).Should().BeLessThanOrEqualTo(38);
    }
}
