using OneCode.App.Tui;

namespace OneCode.Tests;

public sealed class AgentStatusBarTests
{
    [Theory]
    [InlineData("claude-3-7-sonnet-20250219", "Sonnet 3.7")]
    [InlineData("claude-3-5-sonnet-20241022", "Sonnet 3.5")]
    [InlineData("claude-3-5-haiku-20241022", "Haiku 3.5")]
    [InlineData("claude-3-opus-20240229", "Opus")]
    [InlineData("gpt-4o-mini", "GPT-4o-mini")]
    [InlineData("gpt-4o-2024-08-06", "GPT-4o")]
    [InlineData("gpt-4-turbo", "GPT-4T")]
    [InlineData("o3-mini", "o3-mini")]
    [InlineData("o1-preview", "o1")]
    public void ShortenModelName_KnownModels_ReturnsConciseName(string model, string expected)
    {
        AgentStatusBar.ShortenModelName(model).Should().Be(expected);
    }

    [Fact]
    public void ShortenModelName_UnknownLongModel_TruncatesWithEllipsis()
    {
        var model = "custom-deepseek-v3-thinking";
        var result = AgentStatusBar.ShortenModelName(model);
        result.Should().Be("custom-dee" + TuiGlyphs.Ellipsis);
    }

    [Fact]
    public void ShortenModelName_ShortModel_ReturnsUnchanged()
    {
        AgentStatusBar.ShortenModelName("llama3").Should().Be("llama3");
    }
}
