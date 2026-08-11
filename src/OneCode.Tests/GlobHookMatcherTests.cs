using OneCode.App.Services.Hooks;

namespace OneCode.Tests;

/// <summary>
/// Tests for <see cref="GlobHookMatcher"/>.
///
/// The matcher implements the hook system semantics:
///   "" or "*"              → wildcard, matches anything (even empty)
///   "Bash"                 → exact match, case-insensitive
///   "Bash*"                → prefix glob (only single * in the pattern is fast-pathed)
///   "*Tool"                → suffix glob
///   "Write|Read"           → pipe-separated, matches any one
///   "  Read  "             → whitespace is trimmed per-pipe-segment
/// </summary>
public sealed class GlobHookMatcherTests
{
    private readonly GlobHookMatcher _sut = new GlobHookMatcher();

    [Theory]
    [InlineData("")]
    [InlineData("*")]
    public void Matches_Wildcard_MatchesAnything(string pattern)
    {
        _sut.Matches(pattern, "Bash").Should().BeTrue();
        _sut.Matches(pattern, "").Should().BeTrue();
        _sut.Matches(pattern, "anything-at-all").Should().BeTrue();
    }

    [Theory]
    [InlineData("Bash", "Bash")]
    [InlineData("Bash", "bash")]
    [InlineData("BASH", "bash")]
    public void Matches_ExactPattern_IsCaseInsensitive(string pattern, string value)
    {
        _sut.Matches(pattern, value).Should().BeTrue();
    }

    [Fact]
    public void Matches_ExactPattern_DoesNotMatchDifferentValue()
    {
        _sut.Matches("Bash", "Read").Should().BeFalse();
    }

    [Fact]
    public void Matches_PrefixGlob_MatchesValueWithPrefix()
    {
        _sut.Matches("Bash*", "Bash").Should().BeTrue();
        _sut.Matches("Bash*", "BashRun").Should().BeTrue();
    }

    [Fact]
    public void Matches_PrefixGlob_DoesNotMatchOtherPrefix()
    {
        _sut.Matches("Bash*", "Read").Should().BeFalse();
    }

    [Fact]
    public void Matches_SuffixGlob_MatchesValueWithSuffix()
    {
        _sut.Matches("*Tool", "MyTool").Should().BeTrue();
        _sut.Matches("*Tool", "Tool").Should().BeTrue();
    }

    [Fact]
    public void Matches_MultiStarGlob_UsesRegexFallback()
    {
        // pattern has more than one * → regex path
        _sut.Matches("Ba*sh", "Bash").Should().BeTrue();
        _sut.Matches("B*h*", "Bash").Should().BeTrue();
    }

    [Fact]
    public void Matches_PipeSeparator_MatchesAnyOne()
    {
        _sut.Matches("Write|Read", "Write").Should().BeTrue();
        _sut.Matches("Write|Read", "Read").Should().BeTrue();
        _sut.Matches("Write|Read", "Bash").Should().BeFalse();
    }

    [Fact]
    public void Matches_PipeSeparator_TrimsWhitespace()
    {
        _sut.Matches("  Write  |  Read  ", "Write").Should().BeTrue();
        _sut.Matches("  Write  |  Read  ", "Read").Should().BeTrue();
    }

    [Fact]
    public void Matches_NonEmptyPattern_EmptyValue_DoesNotMatch()
    {
        _sut.Matches("Bash", "").Should().BeFalse();
        _sut.Matches("Bash*", "").Should().BeFalse();
    }
}
