using OneCode.Infrastructure;

namespace OneCode.Tests;

/// <summary>
/// ProcessRunner cancel vs timeout semantics (OC-P2-01).
/// </summary>
public sealed class ProcessRunnerTests
{
    [Fact]
    public async Task ExecuteWithTimeoutAsync_Timeout_ReturnsTimedOutTrue()
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = new ProcessRunner();

        // Long ping; short timeout must mark TimedOut without throwing.
        var result = await sut.ExecuteWithTimeoutAsync(
            GetSleepCommand(),
            GetSleepArgs(30),
            timeoutMs: 300,
            ct: ct);

        result.Should().NotBeNull();
        result!.TimedOut.Should().BeTrue();
        result.ExitCode.Should().Be(-1);
    }

    [Fact]
    public async Task ExecuteWithTimeoutAsync_ExternalCancel_ThrowsOperationCanceled()
    {
        var sut = new ProcessRunner();
        using var cts = new CancellationTokenSource();

        var run = sut.ExecuteWithTimeoutAsync(
            GetSleepCommand(),
            GetSleepArgs(30),
            timeoutMs: 60_000,
            ct: cts.Token);

        await Task.Delay(150, TestContext.Current.CancellationToken);
        await cts.CancelAsync();

        var act = async () => await run;
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    private static string GetSleepCommand()
        => OperatingSystem.IsWindows() ? "ping" : "sleep";

    private static string[] GetSleepArgs(int seconds)
        => OperatingSystem.IsWindows()
            ? ["-n", (seconds + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), "127.0.0.1"]
            : [seconds.ToString(System.Globalization.CultureInfo.InvariantCulture)];
}
