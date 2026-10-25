using OneCode.Tui;

namespace OneCode.Tests;

public sealed class LaunchModeDetectorTests
{
    [Theory]
    [InlineData("--version")]
    [InlineData("-v")]
    [InlineData("-V")]
    public void Detect_SingleVersionFlag_ReturnsFastPathVersion(string flag)
    {
        var result = LaunchModeDetector.Detect([flag]);
        result.Should().Be(LaunchMode.FastPathVersion);
    }

    /// <summary>版本旗标只允许单独出现：附带其它参数时必须走完整启动，不得吞掉后续参数。</summary>
    [Fact]
    public void Detect_VersionWithOtherArgs_ReturnsInteractive()
    {
        var result = LaunchModeDetector.Detect(["--version", "--verbose"]);
        result.Should().Be(LaunchMode.Interactive);
    }

    /// <summary>无参数时进入完整交互式启动（缺省路径，不得误入快路径）。</summary>
    [Fact]
    public void Detect_NoArgs_ReturnsInteractive()
    {
        var result = LaunchModeDetector.Detect([]);
        result.Should().Be(LaunchMode.Interactive);
    }
}
