// MemoryEntryStoreTests partial：TTL 过期清理与 50 条上限按时间淘汰（manual 豁免）
using OneCode.Core.Memory;
using OneCode.Infrastructure.Memory;

namespace OneCode.Tests;

public sealed partial class MemoryEntryStoreTests
{
    [Fact]
    public async Task PruneAsync_RemovesExpiredEntries()
    {
        var now = DateTimeOffset.UtcNow;
        var fresh = new MemoryEntry
        {
            Key = "fact:fresh",
            Value = "fresh entry",
            Source = "autodream",
            Category = "fact",
            CreatedAt = now,
            UpdatedAt = now,
            ExpiresAt = now.AddDays(30),
        };
        var expired = new MemoryEntry
        {
            Key = "fact:expired",
            Value = "expired entry",
            Source = "autodream",
            Category = "fact",
            CreatedAt = now.AddDays(-60),
            UpdatedAt = now.AddDays(-60),
            ExpiresAt = now.AddDays(-1),
        };

        await _store.UpsertAsync(MemoryScope.Project, [fresh, expired], default);

        var prunedCount = await _store.PruneAsync(MemoryScope.Project, default);

        prunedCount.Should().Be(1);
        var loaded = await _store.LoadAsync(MemoryScope.Project, default);
        loaded.Should().HaveCount(1);
        loaded[0].Key.Should().Be("fact:fresh");
    }

    [Fact]
    public async Task PruneAsync_KeepsNeverExpiringEntries()
    {
        var now = DateTimeOffset.UtcNow;
        var entry = new MemoryEntry
        {
            Key = "manual:permanent",
            Value = "never expires",
            Source = "manual",
            Category = "manual",
            CreatedAt = now,
            UpdatedAt = now,
            ExpiresAt = null,
        };

        await _store.UpsertAsync(MemoryScope.Project, [entry], default);

        var prunedCount = await _store.PruneAsync(MemoryScope.Project, default);

        prunedCount.Should().Be(0);
        var loaded = await _store.LoadAsync(MemoryScope.Project, default);
        loaded.Should().HaveCount(1);
    }

    [Fact]
    public async Task PruneAsync_EvictsOldestWhenOverLimit()
    {
        var now = DateTimeOffset.UtcNow;
        var entries = new List<MemoryEntry>();

        for (var i = 0; i < MemoryEntryStore.MaxEntries + 10; i++)
        {
            entries.Add(new MemoryEntry
            {
                Key = $"fact:entry-{i:D3}",
                Value = $"value {i}",
                Source = "autodream",
                Category = "fact",
                CreatedAt = now.AddMinutes(-i),
                UpdatedAt = now.AddMinutes(-i),
            });
        }

        await _store.UpsertAsync(MemoryScope.Project, entries, default);

        var prunedCount = await _store.PruneAsync(MemoryScope.Project, default);

        prunedCount.Should().Be(10, "should evict 10 entries to reach MaxEntries (50) limit");
        var loaded = await _store.LoadAsync(MemoryScope.Project, default);
        loaded.Should().HaveCount(MemoryEntryStore.MaxEntries);

        var oldestKey = $"fact:entry-{MemoryEntryStore.MaxEntries + 9:D3}";
        loaded.Should().NotContain(e => e.Key == oldestKey);
        loaded.Should().Contain(e => e.Key == "fact:entry-000");
    }

    /// <summary>
    /// Manual entries are exempt from automatic eviction even when they are the oldest —
    /// they express explicit user intent. The key deliberately carries no <c>manual:</c> prefix
    /// so that only <see cref="MemoryEntry.Source"/> can be the reason the entry survives.
    /// </summary>
    [Fact]
    public async Task PruneAsync_NeverEvictsManualEntries_EvenWhenOldest()
    {
        var now = DateTimeOffset.UtcNow;
        var entries = new List<MemoryEntry>
        {
            new()
            {
                Key = "fact:user-edited",
                Value = "user authored long ago",
                Source = "manual",
                Category = "manual",
                CreatedAt = now.AddDays(-365),
                UpdatedAt = now.AddDays(-365),
            },
        };

        for (var i = 0; i < MemoryEntryStore.MaxEntries + 5; i++)
        {
            entries.Add(new MemoryEntry
            {
                Key = $"fact:filler-{i:D3}",
                Value = $"filler {i}",
                Source = "autodream",
                Category = "fact",
                CreatedAt = now.AddMinutes(-i),
                UpdatedAt = now.AddMinutes(-i),
            });
        }

        await _store.UpsertAsync(MemoryScope.Project, entries, default);

        await _store.PruneAsync(MemoryScope.Project, default);

        var loaded = await _store.LoadAsync(MemoryScope.Project, default);
        loaded.Should().Contain(e => e.Key == "fact:user-edited", "source=manual entries are never auto-evicted");
        loaded.Should().HaveCount(MemoryEntryStore.MaxEntries, "manual entries still consume capacity");
    }
}
