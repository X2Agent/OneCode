using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using OneCode.Automation.Cron;
using OneCode.Core.Cron;

namespace OneCode.Tests;

/// <summary>
/// durable 作业的项目归属过滤与跨进程触发锁的回归守卫。
/// 作业 JSON 在进程间共享磁盘：ProjectDir 过滤防止别项目的进程误触发别项目的作业，
/// FileShare.None 触发锁防止同项目多进程（TUI + daemon）对同一作业双触发。
/// 全部走真实临时目录与真实锁文件（FileShare 语义无法用替身模拟）。
/// </summary>
public sealed class CronFireLockAndProjectFilterTests : IDisposable
{
    private readonly string _cronDir;
    private readonly string _projectDir;
    private readonly ICronParser _parser = Substitute.For<ICronParser>();
    private readonly List<CronSchedulerService> _schedulers = [];

    public CronFireLockAndProjectFilterTests()
    {
        var root = Path.Combine(Path.GetTempPath(), "onecode-cron-tests");
        _cronDir = Path.Combine(root, Guid.NewGuid().ToString("N"));
        _projectDir = Path.Combine(root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_cronDir);
        Directory.CreateDirectory(_projectDir);

        // 触发后 NextRunAt 推进到未来，保证同一 CheckAndFire 周期内不重复触发。
        _parser.ComputeNextRun(Arg.Any<string>(), Arg.Any<DateTimeOffset>())
            .Returns(DateTimeOffset.UtcNow.AddMinutes(5));
    }

    [Fact]
    public async Task TryAddJobAsync_StampsProjectDir_FromSchedulerProject()
    {
        var scheduler = NewScheduler(Substitute.For<ICronJobExecutor>());

        (await scheduler.TryAddJobAsync(NewDueJob("stamp01", durable: false)))
            .Should().BeTrue();

        scheduler.GetJobs().Single().ProjectDir.Should().Be(Path.GetFullPath(_projectDir),
            "durable 作业的磁盘归属由创建进程的调度器盖章，漏盖章会让作业在任意项目触发");
    }

    [Fact]
    public async Task ReloadJobsAsync_DurableJobsFromOtherProject_AreNotLoaded()
    {
        WriteDurableJob("own01", _projectDir);
        WriteDurableJob("other01", Path.Combine(Path.GetTempPath(), "onecode-other-project"));
        WriteDurableJob("legacy01", projectDir: null);
        var scheduler = NewScheduler(Substitute.For<ICronJobExecutor>());

        await scheduler.ReloadJobsAsync(CancellationToken.None);

        var ids = scheduler.GetJobs().Select(static j => j.Id).ToList();
        ids.Should().Contain("own01");
        ids.Should().Contain("legacy01", "旧版 durable JSON 无 ProjectDir，保持跟随任意进程的历史行为");
        ids.Should().NotContain("other01", "别项目的 durable 作业不得进入本项目的调度器");
    }

    [Fact]
    public async Task CheckAndFireJobsAsync_FireLockHeld_SkipsWithoutFiring()
    {
        WriteDurableJob("lock01", _projectDir);
        var executor = Substitute.For<ICronJobExecutor>();
        var scheduler = NewScheduler(executor);
        await scheduler.ReloadJobsAsync(CancellationToken.None);

        var lockPath = Path.Combine(_cronDir, "locks", "lock01.lock");
        Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);
        using (var held = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            await scheduler.CheckAndFireJobsAsync(CancellationToken.None);

            await executor.DidNotReceive().ExecuteJobAsync(
                Arg.Any<string>(), Arg.Any<CancellationToken>());
            scheduler.GetJobs().Single().LastRunAt.Should().BeNull("锁竞争跳过不得产生触发记录");
        }

        // 持锁进程退出（句柄释放）后，下一轮照常触发——锁不得永久卡死作业。
        await scheduler.CheckAndFireJobsAsync(CancellationToken.None);
        await executor.Received(1).ExecuteJobAsync(
            Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CheckAndFireJobsAsync_TwoProcessesSameProject_JobFiresExactlyOnce()
    {
        WriteDurableJob("dup01", _projectDir);
        var executorA = new GatedCronExecutor();
        var executorB = new GatedCronExecutor();
        var schedulerA = NewScheduler(executorA);
        var schedulerB = NewScheduler(executorB);
        await schedulerA.ReloadJobsAsync(CancellationToken.None);
        await schedulerB.ReloadJobsAsync(CancellationToken.None);

        var fireA = schedulerA.CheckAndFireJobsAsync(CancellationToken.None);
        await executorA.Started.WaitAsync(TimeSpan.FromSeconds(10));

        // A 持锁执行期间，同项目另一进程的调度器必须跳过该作业。
        await schedulerB.CheckAndFireJobsAsync(CancellationToken.None);
        executorB.Calls.Should().Be(0, "同项目双进程并存时 durable 作业只允许触发一次");

        executorA.Release();
        await fireA;
        executorA.Calls.Should().Be(1);
    }

    private CronSchedulerService NewScheduler(ICronJobExecutor executor)
    {
        var scheduler = new CronSchedulerService(
            NullLogger<CronSchedulerService>.Instance,
            _parser,
            executor,
            projectDir: _projectDir,
            cronDir: _cronDir);
        _schedulers.Add(scheduler);
        return scheduler;
    }

    private static CronJobEntry NewDueJob(string id, bool durable) => new()
    {
        Id = id,
        Cron = "* * * * *",
        Prompt = $"prompt {id}",
        Recurring = true,
        Durable = durable,
        Paused = false,
        CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        NextRunAt = DateTimeOffset.UtcNow.AddSeconds(-1).ToUnixTimeSeconds(),
    };

    private void WriteDurableJob(string id, string? projectDir)
    {
        var entry = NewDueJob(id, durable: true);
        entry.ProjectDir = projectDir is null ? null : Path.GetFullPath(projectDir);
        File.WriteAllText(Path.Combine(_cronDir, $"{id}.json"), JsonSerializer.Serialize(entry));
    }

    /// <summary>可门控的执行器：进入即计数并发出 <see cref="Started"/>，等待 Release 放行。</summary>
    private sealed class GatedCronExecutor : ICronJobExecutor
    {
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Started => _started.Task;

        public int Calls { get; private set; }

        public async Task ExecuteJobAsync(string prompt, CancellationToken ct)
        {
            Calls++;
            _started.TrySetResult();
            await _release.Task.WaitAsync(ct).ConfigureAwait(false);
        }

        public void Release() => _release.TrySetResult();
    }

    public void Dispose()
    {
        foreach (var scheduler in _schedulers)
            scheduler.Dispose();
        try { Directory.Delete(_cronDir, recursive: true); } catch (IOException) { }
        try { Directory.Delete(_projectDir, recursive: true); } catch (IOException) { }
    }
}
