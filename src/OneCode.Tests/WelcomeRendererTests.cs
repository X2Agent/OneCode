using OneCode.App.Tui;

namespace OneCode.Tests;

/// <summary>
/// 欢迎屏渲染守护：无框居中样式（块状 logo / 版本 / 提示行）、视口垂直居中。
/// </summary>
public sealed class WelcomeRendererTests
{
    [Fact]
    public void Render_ShowsLogoVersionAndHints()
    {
        var lines = WelcomeRenderer.Render(new WelcomeInfo("1.0.0"), viewWidth: 100, viewHeight: 0);

        lines.Should().Contain(line => line.FullText.Contains('█'));
        lines.Should().Contain(line => line.FullText.Trim().Equals("v1.0.0"));
        lines.Should().Contain(line => line.FullText.Contains("斜杠命令") && line.FullText.Contains("/find"));
        lines.Should().Contain(line => line.FullText.Contains("Alt+1..4") && line.FullText.Contains("/keybindings"));
    }

    [Fact]
    public void Render_LogoUsesSecondaryGray()
    {
        var lines = WelcomeRenderer.Render(new WelcomeInfo("1.0.0"), viewWidth: 100, viewHeight: 0);

        lines.Where(line => line.FullText.Contains("█"))
            .Should().OnlyContain(line => line.Color == TuiPalette.FgSecondary);
    }

    [Fact]
    public void Render_VerticallyCentersBlockInViewport()
    {
        var lines = WelcomeRenderer.Render(new WelcomeInfo("1.0.0"), viewWidth: 100, viewHeight: 34);

        // 内容块 12 行，topPad = (34-12)/2 = 11：前 11 行全空白，第 12 行是 logo 首行
        lines.Count.Should().Be(23);
        lines.Take(11).Should().OnlyContain(line => line.FullText.Trim().Length == 0);
        lines[11].FullText.Should().Contain("█");
    }

    [Fact]
    public void Render_ShortViewportKeepsMinimalTopPadding()
    {
        var lines = WelcomeRenderer.Render(new WelcomeInfo("1.0.0"), viewWidth: 60, viewHeight: 14);

        // 视口刚够容纳内容块：只保留 2 行顶部留白
        lines.Count.Should().Be(14);
        lines[2].FullText.Should().Contain("█");
    }
}
