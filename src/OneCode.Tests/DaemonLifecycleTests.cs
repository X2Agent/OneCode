using OneCode.App.Daemon;

namespace OneCode.Tests;

/// <summary>
/// daemon 生命周期协作面的回归守卫：互斥体存活探针、项目 slug 唯一性、状态文件解析兜底。
/// 存活判定是 /daemon start/stop 与状态视图的真相源，探针误报会直接卡死启停流程。
/// </summary>
public sealed class DaemonLifecycleTests
{
    [Fact]
    public void IsRunning_MutexHeldByAnotherHolder_ReturnsTrue()
    {
        var projectDir = Path.Combine(Path.GetTempPath(), $"onecode-daemon-probe-{Guid.NewGuid():N}");
        using var holder = new Mutex(
            initiallyOwned: true, DaemonLifecycle.GetMutexName(projectDir), out var createdNew);
        createdNew.Should().BeTrue("测试前提：先占住互斥体模拟 daemon 持有");

        DaemonLifecycle.IsRunning(projectDir).Should().BeTrue("互斥体存在即有存活持有者");
    }

    [Fact]
    public void IsRunning_NoMutexEverCreated_ReturnsFalse()
    {
        var projectDir = Path.Combine(Path.GetTempPath(), $"onecode-daemon-absent-{Guid.NewGuid():N}");

        DaemonLifecycle.IsRunning(projectDir).Should().BeFalse("从未启动的探测不得误报运行中");
    }

    [Fact]
    public void GetSlug_SameFolderNameDifferentPaths_DoNotCollide()
    {
        var slugA = DaemonLifecycle.GetSlug(Path.Combine(Path.GetTempPath(), "root-a", "MyProject"));
        var slugB = DaemonLifecycle.GetSlug(Path.Combine(Path.GetTempPath(), "root-b", "MyProject"));

        slugA.Should().NotBe(slugB,
            "同名目录不同路径必须得到不同 slug——否则别项目的状态文件/互斥体会互相覆盖");
        DaemonLifecycle.GetSlug(Path.Combine(Path.GetTempPath(), "root-a", "MyProject"))
            .Should().Be(slugA, "同一路径的 slug 必须稳定（跨进程重启可复现）");
    }

    [Fact]
    public void ReadStatusFile_MissingOrCorruptFile_ReturnsNullInsteadOfThrowing()
    {
        var dir = Path.Combine(Path.GetTempPath(), "onecode-daemon-status", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);

        DaemonLifecycle.ReadStatusFile(Path.Combine(dir, "absent.json")).Should().BeNull();

        File.WriteAllText(Path.Combine(dir, "corrupt.json"), "{ not valid json");
        DaemonLifecycle.ReadStatusFile(Path.Combine(dir, "corrupt.json")).Should().BeNull(
            "残留损坏状态文件不得让 /daemon 状态视图崩溃");

        File.WriteAllText(Path.Combine(dir, "valid.json"),
            """{"pid":123,"startedAt":1700000000,"projectDir":"E:/proj"}""");
        var status = DaemonLifecycle.ReadStatusFile(Path.Combine(dir, "valid.json"));
        status.Should().NotBeNull();
        status!.Pid.Should().Be(123);
        status.ProjectDir.Should().Be("E:/proj");
    }
}

/// <summary>
/// 停止哨兵监听的回归守卫：哨兵出现即触发且只触发一次、启动前残留的哨兵同样消费、消费后清理。
/// /daemon stop 的可用性完全依赖这条链路。
/// </summary>
public sealed class DaemonStopWatcherTests : IDisposable
{
    private readonly string _stopFile;

    public DaemonStopWatcherTests()
    {
        var dir = Path.Combine(Path.GetTempPath(), "onecode-daemon-stop", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _stopFile = Path.Combine(dir, "test.stop");
    }

    [Fact]
    public async Task StopFileCreatedAfterWatch_TriggersStopOnceAndConsumesSentinel()
    {
        var fired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        using (new DaemonStopWatcher(_stopFile, () => { Interlocked.Increment(ref calls); fired.TrySetResult(); }))
        {
            File.WriteAllText(_stopFile, string.Empty);
            var completed = await Task.WhenAny(fired.Task, Task.Delay(TimeSpan.FromSeconds(10)));
            completed.Should().BeSameAs(fired.Task, "哨兵落盘必须触发停止回调");

            File.Exists(_stopFile).Should().BeFalse("哨兵被消费后必须清理，否则下次启动即自杀");

            // 已触发后再次创建同路径哨兵不得二次停止（回调只发一次）。
            File.WriteAllText(_stopFile, string.Empty);
            calls.Should().Be(1);
        }
    }

    [Fact]
    public void StopFilePresentBeforeWatch_TriggersImmediately()
    {
        var fired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        File.WriteAllText(_stopFile, string.Empty);

        using (new DaemonStopWatcher(_stopFile, () => fired.TrySetResult()))
        {
            fired.Task.IsCompleted.Should().BeTrue(
                "启动前残留的哨兵是未消费的停止请求，必须在构造时补触发");
        }
    }

    public void Dispose()
    {
        if (File.Exists(_stopFile)) File.Delete(_stopFile);
        try { Directory.Delete(Path.GetDirectoryName(_stopFile)!, recursive: true); }
        catch (IOException) { }
    }
}
