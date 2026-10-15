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
        lines.Should().Contain(line => line.FullText.Contains("/keybindings"));
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

        // 内容块 14 行（7 行 logo + 7 行其余），topPad = (34-14)/2 = 10：前 10 行全空白，第 11 行是 logo 首行
        lines.Count.Should().Be(24);
        lines.Take(10).Should().OnlyContain(line => line.FullText.Trim().Length == 0);
        lines[10].FullText.Should().Contain("█");
    }

    [Fact]
    public void Render_ShortViewportKeepsMinimalTopPadding()
    {
        var lines = WelcomeRenderer.Render(new WelcomeInfo("1.0.0"), viewWidth: 60, viewHeight: 16);

        // 视口刚够容纳内容块：只保留 2 行顶部留白
        lines.Count.Should().Be(16);
        lines[2].FullText.Should().Contain("█");
    }

    /// <summary>
    /// 字体度量防回归：logo 固定 7 行，7 个字母统一 4 列 + 6 个间距各 1 列 = 34 列。
    /// 历史上手写字形曾在此悄悄走形（字宽 3/4 混杂、内腔 1 列），锁死度量防止复发。
    /// </summary>
    [Fact]
    public void Render_LogoIsSevenRowsWithUniformLetterCells()
    {
        var lines = WelcomeRenderer.Render(new WelcomeInfo("1.0.0"), viewWidth: 100, viewHeight: 0);

        var logoLines = lines
            .SkipWhile(line => !line.FullText.Contains('█'))
            .TakeWhile(line => line.FullText.Contains('█'))
            .ToList();

        logoLines.Should().HaveCount(7);
        TextWidthHelper.GetDisplayWidth(logoLines[0].FullText.TrimStart()).Should().Be(7 * 4 + 6);
    }
}
