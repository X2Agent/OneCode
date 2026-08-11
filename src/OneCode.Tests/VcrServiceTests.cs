using OneCode.Infrastructure;

namespace OneCode.Tests;

public sealed class VcrModeParserTests
{
    [Theory]
    [InlineData(null, VcrMode.Inactive)]
    [InlineData("", VcrMode.Inactive)]
    [InlineData("off", VcrMode.Inactive)]
    [InlineData("RECORD", VcrMode.Record)] // Known values are case-insensitive
    [InlineData("replay", VcrMode.Replay)]
    [InlineData("recod", VcrMode.Inactive)] // Unknown values fail-safe to inactive
    public void Parse_ReturnsExpectedMode(string? input, VcrMode expected)
    {
        VcrModeParser.Parse(input).Should().Be(expected);
    }
}
