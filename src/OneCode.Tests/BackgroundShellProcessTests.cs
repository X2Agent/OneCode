using System.Diagnostics;
using OneCode.Infrastructure;

namespace OneCode.Tests;

/// <summary>
/// BackgroundShellProcess 行为测试：正常命令回收输出、
/// 启动失败必须抛出（由调用方映射任务失败语义，不得静默）、取消时杀整树并快速收尾。
/// </summary>
public sealed class BackgroundShellProcessTests
{
    [Fact]
    public async Task RunAsync_PlainCommand_ReturnsOutputAndZeroExit()
    {
        var (exitCode, stdout, stderr) = await BackgroundShellProcess.RunAsync(
            "echo shell-ok", Directory.GetCurrentDirectory(), CancellationToken.None);

        exitCode.Should().Be(0);
        stdout.Trim().Should().Be("shell-ok");
        stderr.Should().BeEmpty();
    }

    [Fact]
    public async Task RunAsync_UnknownWorkingDirectory_ThrowsInsteadOfSilentFailure()
    {
        var missingDir = Path.Combine(Path.GetTempPath(), "onecode-no-such-dir-" + Guid.NewGuid().ToString("N"));

        var act = () => BackgroundShellProcess.RunAsync("echo x", missingDir, CancellationToken.None);

        // 启动失败必须以异常上抛：调用方（后台任务工具）依赖异常把任务标为失败并写入错误输出。
        await act.Should().ThrowAsync<Exception>();
    }

    [Fact]
    public async Task RunAsync_Cancellation_KillsProcessTreeAndThrowsPromptly()
    {
        using var cts = new CancellationTokenSource();
        // 30 秒长命令：取消只能靠杀树生效，若实现只 Cancel 不 Kill，本用例必然超时失败。
        var command = OperatingSystem.IsWindows()
            ? "ping -n 30 127.0.0.1 > nul"
            : "sleep 30";
        var run = Task.Run(() => BackgroundShellProcess.RunAsync(
            command, Directory.GetCurrentDirectory(), cts.Token));

        // 先等长命令子进程真实存活（测试前提：杀的是树，不是空气）。
        var childAlive = await WaitUntilChildAliveAsync(TimeSpan.FromSeconds(10));
        childAlive.Should().BeTrue("测试前提：长命令子进程必须先启动再取消");

        var cancelWatch = Stopwatch.StartNew();
        cts.Cancel();
        await FluentActions.Awaiting(() => run).Should().ThrowAsync<OperationCanceledException>();
        cancelWatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10),
            "取消必须杀进程树立即收尾，而不是等长命令自然退出");

        var childGone = await WaitUntilChildGoneAsync(TimeSpan.FromSeconds(10));
        childGone.Should().BeTrue("杀树后不得残留存活的长命令子进程");
    }

    private static async Task<bool> WaitUntilChildAliveAsync(TimeSpan timeout) =>
        await PollAsync(timeout, () => IsLongCommandChildAlive());

    private static async Task<bool> WaitUntilChildGoneAsync(TimeSpan timeout) =>
        await PollAsync(timeout, () => !IsLongCommandChildAlive());

    private static async Task<bool> PollAsync(TimeSpan timeout, Func<bool> condition)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < timeout)
        {
            if (condition())
                return true;
            await Task.Delay(50);
        }
        return condition();
    }

    private static bool IsLongCommandChildAlive() =>
        Process.GetProcessesByName(OperatingSystem.IsWindows() ? "PING" : "sleep").Length > 0;
}
