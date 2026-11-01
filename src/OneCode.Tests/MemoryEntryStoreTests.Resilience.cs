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

    /// <summary>
    /// 反证读取缓存：外部改写 MEMORY.md 后必须重读到新内容。
    /// 缓存以 (mtime, size) 为凭据——若不校验，agent 会把上一轮的旧记忆反复注入提示词。
    /// </summary>
    [Fact]
    public async Task LoadAllAsync_FileChangedExternally_ReturnsFreshContent()
    {
        var entry = new MemoryEntry
        {
            Key = "fact:cached",
            Value = "original",
            Source = "autodream",
            Category = "fact",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        await _store.UpsertAsync(MemoryScope.Project, [entry], default);
        (await _store.LoadAllAsync(MemoryScope.Project, default)).Single().Value.Should().Be("original");

        // 模拟外部编辑器改写：内容长度不同，必然带来 size 变化。
        var memoryDir = MemdirPaths.ProjectMemoryDir(_projectDir);
        var filePath = MemoryEntryStore.GetFilePath(memoryDir);
        var rewritten = (await File.ReadAllTextAsync(filePath)).Replace("original", "rewritten externally");
        await File.WriteAllTextAsync(filePath, rewritten);
        File.SetLastWriteTimeUtc(filePath, DateTime.UtcNow.AddSeconds(5));

        (await _store.LoadAllAsync(MemoryScope.Project, default)).Single().Value
            .Should().Be("rewritten externally", "外部改写后不得继续返回缓存内容");
    }

    /// <summary>
    /// 反证读取缓存：本类自己的写入必须立刻可见（写入后主动失效，不依赖 mtime 精度）。
    /// </summary>
    [Fact]
    public async Task UpsertAsync_AfterCachedLoad_LatestWriteIsVisible()
    {
        await _store.UpsertAsync(MemoryScope.Project, [CreateEntry("fact:a", "first")], default);
        (await _store.LoadAllAsync(MemoryScope.Project, default)).Should().ContainSingle();

        await _store.UpsertAsync(MemoryScope.Project, [CreateEntry("fact:b", "second")], default);

        (await _store.LoadAllAsync(MemoryScope.Project, default))
            .Select(e => e.Key).Should().BeEquivalentTo(["fact:a", "fact:b"]);
    }

    private static MemoryEntry CreateEntry(string key, string value) => new()
    {
        Key = key,
        Value = value,
        Source = "autodream",
        Category = "fact",
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
    };
}
