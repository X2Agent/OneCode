using OneCode.Cli;

namespace OneCode.Tests;

public sealed class CliModeDetectorTests
{
    [Fact]
    public void Detect_EmptyArgs_ReturnsFullCli()
    {
        var result = CliModeDetector.Detect([]);
        result.Should().Be(CliMode.FullCli);
    }

    [Theory]
    [InlineData("--version")]
    [InlineData("-v")]
    [InlineData("-V")]
    public void Detect_SingleVersionFlag_ReturnsFastPathVersion(string flag)
    {
        var result = CliModeDetector.Detect([flag]);
        result.Should().Be(CliMode.FastPathVersion);
    }

    [Fact]
    public void Detect_VersionWithOtherArgs_ReturnsFullCli()
    {
        var result = CliModeDetector.Detect(["--version", "--verbose"]);
        result.Should().Be(CliMode.FullCli);
    }

    [Fact]
    public void Detect_VersionFlagWithTwoArgs_ReturnsFullCli()
    {
        var result = CliModeDetector.Detect(["--version", "extra"]);
        result.Should().Be(CliMode.FullCli);
    }

    [Fact]
    public void Detect_NonVersionArg_ReturnsFullCli()
    {
        var result = CliModeDetector.Detect(["ps"]);
        result.Should().Be(CliMode.FullCli);
    }
}
