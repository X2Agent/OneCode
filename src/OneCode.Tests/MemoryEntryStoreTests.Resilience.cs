// MemoryEntryStoreTests partial：存储不可读降级与取消传播
using OneCode.Core.Memory;
using OneCode.Infrastructure.Memory;

namespace OneCode.Tests;

public sealed partial class MemoryEntryStoreTests
{
    /// <summary>
    /// 反证：存储存在但读不出时，Upsert 必须拒绝提交，而不是把“读不到”当成“空库”
    /// 然后用只含新条目的内容覆盖掉原有数据。
    /// </summary>
    [Fact]
    public async Task UpsertAsync_StoreUnreadable_RefusesWriteAndLeavesFileIntact()
    {
        var memoryDir = MemdirPaths.ProjectMemoryDir(_projectDir);
        var filePath = MemoryEntryStore.GetFilePath(memoryDir);
        Directory.CreateDirectory(filePath); // 同名目录 → ReadAllTextAsync 必然失败

        var entry = new MemoryEntry
        {
            Key = "fact:new",
            Value = "must not be written",
            Source = "autodream",
            Category = "fact",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        var act = () => _store.UpsertAsync(MemoryScope.Project, [entry], default);

        await act.Should().ThrowAsync<MemoryStoreReadException>();
        Directory.Exists(filePath).Should().BeTrue(
            "a refused write must not replace the unreadable store with a fresh file");
    }

    /// <summary>
    /// 查询路径仍然降级：读不到存储时 <see cref="MemoryEntryStore.LoadAllAsync"/> 返回空列表，
    /// 不让损坏的 MEMORY.md 拖垮提示词注入与 /memory list。
    /// </summary>
    [Fact]
    public async Task LoadAllAsync_StoreUnreadable_DegradesToEmpty()
    {
        var memoryDir = MemdirPaths.ProjectMemoryDir(_projectDir);
        var filePath = MemoryEntryStore.GetFilePath(memoryDir);
        Directory.CreateDirectory(filePath);

        var loaded = await _store.LoadAllAsync(MemoryScope.Project, default);

        loaded.Should().BeEmpty();
    }

    /// <summary>
    /// 取消必须穿透存储层，不能因为读失败就被吞掉。
    /// </summary>
    [Fact]
    public async Task LoadAllAsync_CancelledToken_PropagatesCancellation()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var act = () => _store.LoadAllAsync(MemoryScope.Project, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
