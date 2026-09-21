using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using OneCode.Core.Memory;
using OneCode.Core.Tools;
using OneCode.Infrastructure.Memory;

namespace OneCode.Tests;

/// <summary>
/// M1 行为契约：跨进程提交闸真实生效，且写入不留临时文件。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么不能靠「两个 store 实例并发写」来测。</b> 进程内 <see cref="SemaphoreSlim"/> 是静态的，
/// 同进程的两个实例天然被它串行化——去掉跨进程锁那个用例依然全绿（已反证）。
/// 要真正验证跨进程闸，必须由测试自己占住锁文件，模拟另一个进程。
/// </para>
/// </remarks>
public sealed class MemoryEntryStoreCrossProcessTests : IDisposable
{
    private readonly string _projectDir;
    private readonly string _memoryDir;

    public MemoryEntryStoreCrossProcessTests()
    {
        _projectDir = Path.Combine(Path.GetTempPath(), $"MemoryCross_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_projectDir);
        _memoryDir = MemdirPaths.ProjectMemoryDir(_projectDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_projectDir, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>
    /// 反证核心：另一进程持有锁文件时，写入必须等待而不是直接读改写。
    /// 去掉跨进程锁后本用例失败——写入会在对方持有期间完成。
    /// </summary>
    [Fact]
    public async Task Write_WhileAnotherProcessHoldsLock_WaitsForRelease()
    {
        var store = CreateStore();

        Directory.CreateDirectory(_memoryDir);

        // Simulate a second process: hold the lock file with exclusive sharing.
        using var foreignLock = new FileStream(
            Path.Combine(_memoryDir, ".MEMORY.lock"),
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

        var write = store.UpsertAsync(MemoryScope.Project, [CreateEntry("fact:blocked")]);

        // While the foreign holder is alive the write must not have completed.
        await Task.Delay(200);
        write.IsCompleted.Should().BeFalse(
            "the writer must wait for the cross-process lock, not proceed against a stale baseline");

        foreignLock.Dispose();
        await write;

        (await store.LoadAsync(MemoryScope.Project)).Should().ContainSingle();
    }

    /// <summary>
    /// 反证：锁文件被长期占用时必须有界失败，而不是无限挂住 Agent。
    /// </summary>
    [Fact]
    public async Task Write_WhileLockHeldBeyondRetryLimit_FailsInsteadOfHanging()
    {
        var store = CreateStore();

        Directory.CreateDirectory(_memoryDir);
        using var foreignLock = new FileStream(
            Path.Combine(_memoryDir, ".MEMORY.lock"),
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

        var act = () => store.UpsertAsync(MemoryScope.Project, [CreateEntry("fact:stuck")]);

        await act.Should().ThrowAsync<IOException>(
            "a stuck peer must surface as a failure rather than blocking the run indefinitely");
    }

    /// <summary>锁被释放后，同一个 store 仍能正常写入——等待路径不得破坏状态。</summary>
    [Fact]
    public async Task Write_AfterLockReleased_Succeeds()
    {
        var store = CreateStore();

        await store.UpsertAsync(MemoryScope.Project, [CreateEntry("fact:first")]);

        await store.UpsertAsync(MemoryScope.Project, [CreateEntry("fact:second")]);

        (await store.LoadAsync(MemoryScope.Project)).Should().HaveCount(2);
    }

    /// <summary>临时文件必须是每次写入独有并被原子替换消费，不留残留。</summary>
    [Fact]
    public async Task SuccessfulWrites_LeaveNoTempFiles()
    {
        var store = CreateStore();

        await store.UpsertAsync(MemoryScope.Project, [CreateEntry("fact:clean")]);

        Directory.EnumerateFiles(_memoryDir, "*.tmp")
            .Should().BeEmpty("temp files are per-write and consumed by the atomic replace");
    }

    /// <summary>并发写同一 key 的结果必须是某一次完整写入，不能交错。</summary>
    [Fact]
    public async Task ConcurrentWriters_SameKey_LeaveParsableConsistentFile()
    {
        var writerA = CreateStore();
        var writerB = CreateStore();

        await Task.WhenAll(
            writerA.UpsertAsync(MemoryScope.Project, [CreateEntry("fact:shared", "from A")]),
            writerB.UpsertAsync(MemoryScope.Project, [CreateEntry("fact:shared", "from B")]));

        var entries = await writerA.LoadAsync(MemoryScope.Project);

        entries.Should().ContainSingle("both writers target one key");
        entries[0].Value.Should().BeOneOf("from A", "from B");
    }

    private MemoryEntryStore CreateStore() =>
        new(CreateWorkingDirectory(), NullLogger<MemoryEntryStore>.Instance);

    private IWorkingDirectoryAccessor CreateWorkingDirectory()
    {
        var accessor = Substitute.For<IWorkingDirectoryAccessor>();
        accessor.WorkingDirectory.Returns(_projectDir);
        accessor.AdditionalDirectories.Returns((IReadOnlyList<string>?)null);
        return accessor;
    }

    private static MemoryEntry CreateEntry(string key, string? value = null)
    {
        var now = DateTimeOffset.UtcNow;
        return new MemoryEntry
        {
            Key = key,
            Value = value ?? $"value for {key}",
            Source = "autodream",
            Category = MemoryEntry.DeriveCategory(key),
            CreatedAt = now,
            UpdatedAt = now,
        };
    }
}
