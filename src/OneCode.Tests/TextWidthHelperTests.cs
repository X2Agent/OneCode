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
}
