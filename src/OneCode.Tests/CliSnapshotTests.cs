using OneCode.Cli;

namespace OneCode.Tests;

public sealed class CliSnapshotTests
{
    [Fact]
    public async Task FastPathVersion_ProducesExpectedFormat()
    {
        using var sw = new StringWriter();
        var originalOut = Console.Out;
        Console.SetOut(sw);

        try
        {
            var result = await FastPathDispatcher.DispatchAsync([], CliMode.FastPathVersion);
            result.Should().Be(0);

            var output = sw.ToString();
            output.Should().Contain("OneCode");
        }
        finally
        {
            Console.SetOut(originalOut);
        }
    }
}
