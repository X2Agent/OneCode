using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using OneCode.App.Commands;
using OneCode.App.Daemon;
using OneCode.Automation.Cron;
using OneCode.Core.Commands;
using OneCode.Core.Cron;

namespace OneCode.Tests;

/// <summary>
/// /daemon 命令的状态渲染与启动门禁回归守卫：已运行时不重复拉进程、未运行时给出常驻指引。
/// 进程真实拉起与退出属端到端链路（验收场景），不在单测范围。
/// </summary>
/// <remarks>
/// 标记为 Collection 以串行化执行：命令以 Environment.CurrentDirectory 定位项目，
/// 并行执行会导致工作目录竞争。
/// </remarks>
[Collection(nameof(CurrentDirectoryCollection))]
public sealed class DaemonCommandTests : IDisposable
{
    private readonly string _projectDir;
    private readonly string _cronDir;
    private readonly string _originalCwd;
    private readonly Mutex _holder;
    private readonly CronSchedulerService _scheduler;
    private readonly DaemonCommand _command;

    public DaemonCommandTests()
    {
        var root = Path.Combine(Path.GetTempPath(), "onecode-daemon-cmd");
        _projectDir = Path.Combine(root, Guid.NewGuid().ToString("N"));
        _cronDir = Path.Combine(root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_projectDir);
        Directory.CreateDirectory(_cronDir);
        _originalCwd = Environment.CurrentDirectory;
        Directory.SetCurrentDirectory(_projectDir);

        _holder = new Mutex(
            initiallyOwned: true, DaemonLifecycle.GetMutexName(_projectDir), out var createdNew);
        createdNew.Should().BeTrue("测试前提：独占该项目的 daemon 互斥体");

        _scheduler = new CronSchedulerService(
            NullLogger<CronSchedulerService>.Instance,
            Substitute.For<ICronParser>(),
            Substitute.For<ICronJobExecutor>(),
            projectDir: _projectDir,
            cronDir: _cronDir);
        _command = new DaemonCommand(_scheduler);
    }

    [Fact]
    public async Task Start_AlreadyRunning_ReportsWithoutSpawning()
    {
        var result = await _command.ExecuteAsync(["start"]);

        var text = result.Should().BeOfType<CommandResult.TextResult>().Subject.Value;
        text.Should().Contain("已在运行", "互斥体被持有时不得重复拉起 daemon 进程");
    }

    [Fact]
    public async Task Status_Running_ShowsRunningLineWithoutHint()
    {
        var result = await _command.ExecuteAsync([]);

        var text = result.Should().BeOfType<CommandResult.TextResult>().Subject.Value;
        text.Should().Contain("daemon 运行中");
        text.Should().NotContain("install-daemon.ps1", "运行中时不再展示安装指引");
    }

    [Fact]
    public async Task Status_NotRunning_ShowsInstallHint()
    {
        // 释放并关闭句柄：命名互斥体随最后一个句柄销毁，仅 Release 仍保留存在性。
        _holder.ReleaseMutex();
        _holder.Dispose();

        var result = await _command.ExecuteAsync(["status"]);

        var text = result.Should().BeOfType<CommandResult.TextResult>().Subject.Value;
        text.Should().Contain("daemon 未运行");
        text.Should().Contain("install-daemon.ps1", "未运行时必须给出常驻安装指引");
        text.Should().Contain("无 durable cron 作业", "本项目临时 cron 目录为空");
    }

    public void Dispose()
    {
        _scheduler.Dispose();
        try { _holder.ReleaseMutex(); }
        catch (Exception ex) when (ex is ApplicationException or ObjectDisposedException) { }
        _holder.Dispose();
        try { Directory.SetCurrentDirectory(_originalCwd); } catch { }
        try { Directory.Delete(_projectDir, recursive: true); } catch (IOException) { }
        try { Directory.Delete(_cronDir, recursive: true); } catch (IOException) { }
    }
}
