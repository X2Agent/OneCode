using OneCode.App.Tui;

namespace OneCode.Tests;

public sealed class TuiContentLayoutTests
{
    /// <summary>
    /// 首次绘制前 viewport 未测量，必须回退到 80 列而不是 0（0 会导致换行宽度归零）。
    /// 直通分支（80→80 等）是恒等断言，无回归价值，不测。
    /// </summary>
    [Fact]
    public void GetContentColumnWidth_UnmeasuredViewport_FallsBackToDefaultWidth()
    {
        TuiSpacing.GetContentColumnWidth(0).Should().Be(TuiSpacing.DefaultContentWidth);
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

    // 欢迎页 MCP 状态行已去重移除（2026-09）：状态栏是三态唯一实时出口，
    // 失败明细由一次性 startup hint 给出——欢迎页出现任何 MCP 文案即回归。

    [Fact]
    public void WelcomeRenderer_NeverRendersMcpStatus_DeduplicatedIntoStatusBar()
    {
        const int width = 100;
        var lines = WelcomeRenderer.Render(new WelcomeInfo("1.0.0"), width);

        lines.Select(l => l.FullText).Should().NotContain(t => t.Contains("MCP", StringComparison.Ordinal),
            "MCP tri-state must be surfaced only by the status bar; the welcome screen must not duplicate it");
    }
}
