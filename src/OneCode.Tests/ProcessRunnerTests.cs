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

    [Fact]
    public async Task CommandExistsAsync_UnknownCommand_ReturnsFalseWithoutThrowing()
    {
        // CliWrap 将进程启动失败包装为外层 Win32Exception(0x80004005) + 内层 Win32Exception(2)，
        // 过滤器必须能识别该形态：命令探测应静默降级返回 false，而非抛出异常。
        var sut = new ProcessRunner();

        var exists = await sut.CommandExistsAsync("definitely-not-a-real-command-0e1f2c");

        exists.Should().BeFalse();
    }

    private static string GetSleepCommand()
        => OperatingSystem.IsWindows() ? "ping" : "sleep";

    private static string[] GetSleepArgs(int seconds)
        => OperatingSystem.IsWindows()
            ? ["-n", (seconds + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), "127.0.0.1"]
            : [seconds.ToString(System.Globalization.CultureInfo.InvariantCulture)];
}
