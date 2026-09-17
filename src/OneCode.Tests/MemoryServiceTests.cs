using Microsoft.Extensions.Logging.Abstractions;
using OneCode.App.Services.Memory;
using OneCode.Core.Memory;

namespace OneCode.Tests;

public sealed class MemoryServiceTests
{
    [Fact]
    public async Task FindRelevantMemoriesAsync_WhenQueryDoesNotMatch_ReturnsNoEntries()
    {
        var store = new InMemoryMemoryEntryStore();
        var now = DateTimeOffset.UtcNow;
        await store.UpsertAsync(MemoryScope.Project,
        [
            new MemoryEntry
            {
                Key = "build-command",
                Value = "dotnet test src/OneCode.slnx",
                Source = "manual",
                Category = "workflow",
                CreatedAt = now,
                UpdatedAt = now,
            },
        ]);

        var service = new MemoryService(NullLogger<MemoryService>.Instance, store);

        var matches = await service.FindRelevantMemoriesAsync(
            "unrelated database migration");

        matches.Should().BeEmpty();
    }

    /// <summary>
    /// <c>LoadMemoryPromptAsync</c> 每轮都注入全部条目，属于**被动注入**——它不得报告使用反馈。
    /// 若此路径也写 <c>HitCount</c>，每个条目每轮都会 +1，真实检索信号会被淹没，
    /// 淘汰排序随之退化为噪声（ADR 0004 §11.2 约束 2）。
    /// </summary>
    [Fact]
    public async Task LoadMemoryPromptAsync_DoesNotRecordHits()
    {
        var store = new InMemoryMemoryEntryStore();
        var now = DateTimeOffset.UtcNow;
        await store.UpsertAsync(MemoryScope.Project,
        [
            new MemoryEntry
            {
                Key = "fact:build-command",
                Value = "dotnet build src/OneCode.slnx",
                Source = "autodream",
                Category = "fact",
                CreatedAt = now,
                UpdatedAt = now,
            },
        ]);

        var service = new MemoryService(NullLogger<MemoryService>.Instance, store);

        var prompt = await service.LoadMemoryPromptAsync();

        prompt.Should().NotBeNull("the entry must actually be injected, otherwise this test proves nothing");
        // 自动提取的条目在摘要索引中渲染为 `[category] 首行`（不含 key），故按值断言。
        prompt.Should().Contain("dotnet build src/OneCode.slnx");
        var reloaded = await store.LoadAllAsync(MemoryScope.Project);
        reloaded.Should().ContainSingle()
            .Which.HitCount.Should().Be(0, "passive prompt injection must not inflate hit counts");
    }
}