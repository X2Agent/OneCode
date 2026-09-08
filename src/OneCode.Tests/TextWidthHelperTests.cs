using OneCode.App.Tui;

namespace OneCode.Tests;

/// <summary>
/// 绘制期硬裁剪 ClipByWidth 的边界行为。防回归点：裁剪不得为省略号预留列，
/// 否则恰好满宽的行（换行后的工具 JSON 详情每行如此）行尾字符会被替换成 "…"，
/// 呈现为右侧显示不全。
/// </summary>
public sealed class TextWidthHelperTests
{
    [Fact]
    public void ClipByWidth_TextExactlyAtWidth_KeepsEveryCharacter()
    {
        var text = new string('x', 20);

        var clipped = TextWidthHelper.ClipByWidth(text, 20);

        clipped.Should().Be(text);
    }

    [Fact]
    public void ClipByWidth_OverWideText_ClipsWithoutEllipsis()
    {
        var text = new string('x', 30);

        var clipped = TextWidthHelper.ClipByWidth(text, 20);

        clipped.Should().Be(new string('x', 20));
        clipped.Should().NotContain("\u2026");
    }

    [Fact]
    public void ClipByWidth_WideCharStraddlingBoundary_DroppedWhole()
    {
        // "ab中" 占 1+1+2 列；宽度 3 放不下"中"，整字丢弃而非切半绘制。
        var clipped = TextWidthHelper.ClipByWidth("ab中", 3);

        clipped.Should().Be("ab");
        TextWidthHelper.GetDisplayWidth(clipped).Should().BeLessThanOrEqualTo(3);
    }

    [Fact]
    public void ClipByWidth_CjkWithinWidth_KeepsAllCharacters()
    {
        var text = "中文内容";

        TextWidthHelper.ClipByWidth(text, 8).Should().Be(text);
    }

    [Fact]
    public void ClipByWidth_SurrogatePairStraddlingBoundary_DroppedWhole()
    {
        // "a😀" 占 1+2 列；宽度 2 放不下代理对，整对丢弃。
        var clipped = TextWidthHelper.ClipByWidth("a\U0001F600", 2);

        clipped.Should().Be("a");
    }

    [Fact]
    public void ClipByWidth_NonPositiveWidth_ReturnsEmpty()
    {
        TextWidthHelper.ClipByWidth("abc", 0).Should().BeEmpty();
    }

    // 孤立代理项非合法 Unicode 标量值，AddStr 会令 Terminal.Gui 抛 ArgumentException 整屏崩溃。

    [Fact]
    public void ClipByWidth_ValidSurrogatePairs_KeptWhole()
    {
        var text = "a\U0001F600b"; // a + 😀(代理对) + b

        TextWidthHelper.ClipByWidth(text, 10).Should().Be(text);
    }

    [Fact]
    public void ClipByWidth_LoneSurrogates_Removed()
    {
        // 孤立 high surrogate + 正常文本 + 孤立 low surrogate
        var text = "\uD83Da中b\uDE00";

        TextWidthHelper.ClipByWidth(text, 10).Should().Be("a中b");
    }

    [Fact]
    public void ClipByWidth_CleanText_ReturnsUnchanged()
    {
        const string text = "普通中文 text with emoji 😀";

        TextWidthHelper.ClipByWidth(text, 40).Should().Be(text);
    }

    [Fact]
    public void ClipByWidth_LoneSurrogate_StrippedBeforeClipping()
    {
        // 回归：渲染含孤立代理项的行曾整屏崩溃。剔除后按显示宽度裁剪。
        var text = "\uD83D处理工具结果";

        var clipped = TextWidthHelper.ClipByWidth(text, 5);

        clipped.Should().Be("处理");
    }

    [Fact]
    public void ClipByWidth_OnlyLoneSurrogate_ReturnsEmpty()
    {
        TextWidthHelper.ClipByWidth("\uDE00ab", 10).Should().Be("ab");
    }

    [Fact]
    public void GetDisplayWidth_LoneSurrogate_CountsZero()
    {
        // 渲染层剔除孤立代理项，测量层必须记 0 才能与绘制一致。
        TextWidthHelper.GetDisplayWidth("a\uD83Db").Should().Be(2);
    }

    [Fact]
    public void GetDisplayWidth_SurrogatePairStillCountsTwo()
    {
        TextWidthHelper.GetDisplayWidth("a\U0001F600b").Should().Be(4);
    }
}
