using OneCode.Core.Config;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using OneCode.App.Services.AutoDream;
using OneCode.App.Tools;
using OneCode.Core.Memory;
using OneCode.Core.Models;
using OneCode.Core.Prompt;
using OneCode.Core.Tools;
using OneCode.Infrastructure.Memory;
using System.Diagnostics;

namespace OneCode.Tests;

/// <summary>
/// Tests for <see cref="MemoryEntryStore"/>: MEMORY.md parsing, serialization, upsert,
/// remove, prune (TTL expiry + usage-ranked eviction) and hit-count feedback.
/// </summary>
/// <remarks>
/// Uses a <see cref="TestWorkingDirectoryAccessor"/> that points Project scope to a temp dir,
/// so tests never touch the real <c>~/.onecode/memory/</c> directory. User-scope tests use
/// the in-memory <see cref="InMemoryMemoryEntryStore"/> to avoid polluting the global store.
/// </remarks>
public sealed class MemoryEntryStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _projectDir;
    private readonly TestWorkingDirectoryAccessor _wdAccessor;
    private readonly MemoryEntryStore _store;

    public MemoryEntryStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"MemoryStore_{Guid.NewGuid():N}");
        _projectDir = Path.Combine(_tempDir, "project");
        Directory.CreateDirectory(_projectDir);
        _wdAccessor = new TestWorkingDirectoryAccessor(_projectDir);
        _store = new MemoryEntryStore(_wdAccessor, NullLogger<MemoryEntryStore>.Instance);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch (Exception ex) { Debug.WriteLine($"Cleanup failed: {ex.Message}"); }
    }

    // Round-trip: write → read

    [Fact]
    public async Task UpsertAsync_Then_LoadAsync_RoundTripsEntries()
    {
        var now = DateTimeOffset.UtcNow;
        var entries = new[]
        {
            new MemoryEntry
            {
                Key = "fact:build-command",
                Value = "dotnet build src/OneCode.slnx",
                Source = "autodream",
                Category = "fact",
                CreatedAt = now,
                UpdatedAt = now,
            },
            new MemoryEntry
            {
                Key = "manual:oauth-dpapi",
                Value = "Use DPAPI to encrypt credentials",
                Source = "manual",
                Category = "manual",
                CreatedAt = now,
                UpdatedAt = now,
            },
        };

        await _store.UpsertAsync(MemoryScope.Project, entries, default);

        var loaded = await _store.LoadAsync(MemoryScope.Project, default);

        loaded.Should().HaveCount(2);
        loaded.Should().Contain(e => e.Key == "fact:build-command");
        loaded.Should().Contain(e => e.Key == "manual:oauth-dpapi");
    }

    // Multi-line value preservation

    [Fact]
    public async Task LoadAsync_PreservesMultiLineValues()
    {
        var now = DateTimeOffset.UtcNow;
        var entry = new MemoryEntry
        {
            Key = "lesson:multi-line",
            Value = "Line 1\nLine 2\nLine 3",
            Source = "autodream",
            Category = "lesson",
            CreatedAt = now,
            UpdatedAt = now,
        };

        await _store.UpsertAsync(MemoryScope.Project, [entry], default);

        var loaded = await _store.LoadAsync(MemoryScope.Project, default);

        loaded.Should().HaveCount(1);
        loaded[0].Value.Should().Be("Line 1\nLine 2\nLine 3");
    }

    // Upsert overwrites by key, preserves CreatedAt

    [Fact]
    public async Task UpsertAsync_OverwritesExistingKey_PreservesCreatedAt()
    {
        var originalTime = DateTimeOffset.UtcNow.AddDays(-5);
        var original = new MemoryEntry
        {
            Key = "fact:build",
            Value = "old value",
            Source = "autodream",
            Category = "fact",
            CreatedAt = originalTime,
            UpdatedAt = originalTime,
        };

        await _store.UpsertAsync(MemoryScope.Project, [original], default);

        var updateTime = DateTimeOffset.UtcNow;
        var updated = new MemoryEntry
        {
            Key = "fact:build",
            Value = "new value",
            Source = "autodream",
            Category = "fact",
            CreatedAt = updateTime,
            UpdatedAt = updateTime,
        };

        await _store.UpsertAsync(MemoryScope.Project, [updated], default);

        var loaded = await _store.LoadAsync(MemoryScope.Project, default);

        loaded.Should().HaveCount(1);
        loaded[0].Value.Should().Be("new value");
        loaded[0].CreatedAt.Should().BeCloseTo(originalTime, TimeSpan.FromSeconds(1));
        loaded[0].UpdatedAt.Should().BeCloseTo(updateTime, TimeSpan.FromSeconds(1));
    }

    // Remove

    [Fact]
    public async Task RemoveAsync_DeletesEntryByKey()
    {
        var now = DateTimeOffset.UtcNow;
        await _store.UpsertAsync(MemoryScope.Project, [
            new MemoryEntry { Key = "fact:a", Value = "A", Source = "autodream", Category = "fact", CreatedAt = now, UpdatedAt = now },
            new MemoryEntry { Key = "fact:b", Value = "B", Source = "autodream", Category = "fact", CreatedAt = now, UpdatedAt = now },
        ], default);

        var removed = await _store.RemoveAsync(MemoryScope.Project, "fact:a", default);

        removed.Should().BeTrue();
        var loaded = await _store.LoadAsync(MemoryScope.Project, default);
        loaded.Should().HaveCount(1);
        loaded[0].Key.Should().Be("fact:b");
    }

    [Fact]
    public async Task RemoveAsync_ReturnsFalse_WhenKeyNotFound()
    {
        var result = await _store.RemoveAsync(MemoryScope.Project, "nonexistent", default);
        result.Should().BeFalse();
    }

    // Clear

    [Fact]
    public async Task ClearAsync_RemovesAllEntries()
    {
        var now = DateTimeOffset.UtcNow;
        await _store.UpsertAsync(MemoryScope.Project, [
            new MemoryEntry { Key = "fact:a", Value = "A", Source = "autodream", Category = "fact", CreatedAt = now, UpdatedAt = now },
        ], default);

        await _store.ClearAsync(MemoryScope.Project, default);

        var loaded = await _store.LoadAsync(MemoryScope.Project, default);
        loaded.Should().BeEmpty();
    }

    // Prune: TTL expiry

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

    // Prune: capacity eviction by age (all hit counts zero)

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

        prunedCount.Should().Be(10, "should evict 10 entries to reach MaxEntries limit");
        var loaded = await _store.LoadAsync(MemoryScope.Project, default);
        loaded.Should().HaveCount(MemoryEntryStore.MaxEntries);

        loaded.Should().NotContain(e => e.Key == "fact:entry-209");
        loaded.Should().Contain(e => e.Key == "fact:entry-000");
    }

    // Prune: usage-ranked eviction (hit count outranks age)

    /// <summary>
    /// Retention ranks by <see cref="MemoryEntry.HitCount"/> before age, so a well-used old entry
    /// survives while never-recalled newer entries are evicted.
    /// </summary>
    [Fact]
    public async Task PruneAsync_KeepsFrequentlyHitEntries_OverNewerNeverHitOnes()
    {
        var now = DateTimeOffset.UtcNow;
        var entries = new List<MemoryEntry>
        {
            // Oldest entry, but recalled many times → must survive.
            new()
            {
                Key = "fact:well-used",
                Value = "recalled often",
                Source = "autodream",
                Category = "fact",
                CreatedAt = now.AddDays(-90),
                UpdatedAt = now.AddDays(-90),
                HitCount = 50,
            },
        };

        // Newer entries that were never recalled → these are the eviction candidates.
        for (var i = 0; i < MemoryEntryStore.MaxEntries; i++)
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
        loaded.Should().Contain(e => e.Key == "fact:well-used",
            "hit count must outrank age — a 90-day-old entry recalled 50 times survives");
    }

    /// <summary>
    /// Among equal hit counts, the oldest entry goes first. This is the tie-break that
    /// <c>RecordHitsAsync</c> must not disturb by bumping <see cref="MemoryEntry.UpdatedAt"/>.
    /// </summary>
    [Fact]
    public async Task PruneAsync_EvictsOldestFirst_WhenHitCountsAreEqual()
    {
        var now = DateTimeOffset.UtcNow;
        var entries = new List<MemoryEntry>();

        for (var i = 0; i < MemoryEntryStore.MaxEntries + 5; i++)
        {
            entries.Add(new MemoryEntry
            {
                Key = $"fact:tied-{i:D3}",
                Value = $"value {i}",
                Source = "autodream",
                Category = "fact",
                CreatedAt = now.AddMinutes(-i),
                UpdatedAt = now.AddMinutes(-i),
                HitCount = 7,
            });
        }

        await _store.UpsertAsync(MemoryScope.Project, entries, default);

        await _store.PruneAsync(MemoryScope.Project, default);

        var loaded = await _store.LoadAsync(MemoryScope.Project, default);
        loaded.Should().HaveCount(MemoryEntryStore.MaxEntries);
        loaded.Should().NotContain(e => e.Key == "fact:tied-209");
        loaded.Should().Contain(e => e.Key == "fact:tied-000");
    }

    /// <summary>
    /// Manual entries are exempt from automatic eviction even when they are the oldest and
    /// least-recalled — they express explicit user intent.
    /// </summary>
    [Fact]
    public async Task PruneAsync_NeverEvictsManualEntries_EvenWhenOldestAndNeverHit()
    {
        var now = DateTimeOffset.UtcNow;
        var entries = new List<MemoryEntry>
        {
            new()
            {
                Key = "manual:user-pinned",
                Value = "user authored long ago, never recalled",
                Source = "manual",
                Category = "manual",
                CreatedAt = now.AddDays(-365),
                UpdatedAt = now.AddDays(-365),
                HitCount = 0,
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
        loaded.Should().Contain(e => e.Key == "manual:user-pinned");
    }

    // Usage feedback

    [Fact]
    public async Task RecordHitsAsync_IncrementsHitCount_AndStampsLastHitAt()
    {
        var created = DateTimeOffset.UtcNow.AddDays(-10);
        await _store.UpsertAsync(MemoryScope.Project, [
            new MemoryEntry
            {
                Key = "fact:recalled",
                Value = "value",
                Source = "autodream",
                Category = "fact",
                CreatedAt = created,
                UpdatedAt = created,
            },
        ], default);

        await _store.RecordHitsAsync(MemoryScope.Project, ["fact:recalled"], default);

        var loaded = await _store.LoadAsync(MemoryScope.Project, default);
        loaded[0].HitCount.Should().Be(1);
        loaded[0].LastHitAt.Should().NotBeNull();
    }

    /// <summary>
    /// Usage is not a content change: bumping <see cref="MemoryEntry.UpdatedAt"/> would let a
    /// frequently-recalled entry look "fresh" and escape the eviction tie-break.
    /// </summary>
    [Fact]
    public async Task RecordHitsAsync_DoesNotBumpUpdatedAt()
    {
        var created = DateTimeOffset.UtcNow.AddDays(-10);
        await _store.UpsertAsync(MemoryScope.Project, [
            new MemoryEntry
            {
                Key = "fact:untouched-timestamp",
                Value = "value",
                Source = "autodream",
                Category = "fact",
                CreatedAt = created,
                UpdatedAt = created,
            },
        ], default);

        await _store.RecordHitsAsync(MemoryScope.Project, ["fact:untouched-timestamp"], default);

        var loaded = await _store.LoadAsync(MemoryScope.Project, default);
        loaded[0].HitCount.Should().Be(1);
        loaded[0].UpdatedAt.Should().BeCloseTo(created, TimeSpan.FromSeconds(1),
            "usage feedback must not make an entry look recently updated");
    }

    [Fact]
    public async Task RecordHitsAsync_AccumulatesAcrossCalls()
    {
        var created = DateTimeOffset.UtcNow;
        await _store.UpsertAsync(MemoryScope.Project, [
            new MemoryEntry
            {
                Key = "fact:repeat",
                Value = "value",
                Source = "autodream",
                Category = "fact",
                CreatedAt = created,
                UpdatedAt = created,
            },
        ], default);

        await _store.RecordHitsAsync(MemoryScope.Project, ["fact:repeat"], default);
        await _store.RecordHitsAsync(MemoryScope.Project, ["fact:repeat"], default);
        await _store.RecordHitsAsync(MemoryScope.Project, ["fact:repeat"], default);

        var loaded = await _store.LoadAsync(MemoryScope.Project, default);
        loaded[0].HitCount.Should().Be(3);
    }

    [Fact]
    public async Task RecordHitsAsync_IgnoresUnknownKeys()
    {
        var created = DateTimeOffset.UtcNow;
        await _store.UpsertAsync(MemoryScope.Project, [
            new MemoryEntry
            {
                Key = "fact:known",
                Value = "value",
                Source = "autodream",
                Category = "fact",
                CreatedAt = created,
                UpdatedAt = created,
            },
        ], default);

        await _store.RecordHitsAsync(MemoryScope.Project, ["fact:known", "fact:missing"], default);

        var loaded = await _store.LoadAsync(MemoryScope.Project, default);
        loaded.Should().HaveCount(1);
        loaded[0].HitCount.Should().Be(1);
    }

    // Usage-feedback persistence (backward compatible)

    [Fact]
    public async Task RecordHitsAsync_PersistsHitCountAcrossReload()
    {
        var created = DateTimeOffset.UtcNow;
        await _store.UpsertAsync(MemoryScope.Project, [
            new MemoryEntry
            {
                Key = "fact:persisted",
                Value = "value",
                Source = "autodream",
                Category = "fact",
                CreatedAt = created,
                UpdatedAt = created,
            },
        ], default);

        await _store.RecordHitsAsync(MemoryScope.Project, ["fact:persisted"], default);

        // Fresh store instance reads the same MEMORY.md from disk.
        var reopened = new MemoryEntryStore(_wdAccessor, NullLogger<MemoryEntryStore>.Instance);
        var loaded = await reopened.LoadAsync(MemoryScope.Project, default);

        loaded[0].HitCount.Should().Be(1);
        loaded[0].LastHitAt.Should().NotBeNull();
    }

    /// <summary>
    /// MEMORY.md files written before usage feedback existed must still parse, reading as
    /// never-recalled rather than failing the whole entry.
    /// </summary>
    [Fact]
    public async Task LoadAsync_ParsesLegacyFile_WithoutHitFields_AsZeroHits()
    {
        var memoryDir = MemdirPaths.ProjectMemoryDir(_projectDir);
        Directory.CreateDirectory(memoryDir);
        var filePath = Path.Combine(memoryDir, "MEMORY.md");

        const string legacy = """
            ---
            last_updated: 2024-07-16T10:00:00Z
            entry_count: 1
            ---

            ## fact:legacy-entry

            - source: autodream
            - category: fact
            - created_at: 2024-07-15T10:00:00Z
            - updated_at: 2024-07-16T10:00:00Z

            Legacy body without usage fields.
            """;

        await File.WriteAllTextAsync(filePath, legacy);

        var loaded = await _store.LoadAsync(MemoryScope.Project, default);

        loaded.Should().HaveCount(1);
        loaded[0].Key.Should().Be("fact:legacy-entry");
        loaded[0].HitCount.Should().Be(0);
        loaded[0].LastHitAt.Should().BeNull();
    }

    // LoadAsync filters expired

    [Fact]
    public async Task LoadAsync_FiltersExpiredEntries()
    {
        var now = DateTimeOffset.UtcNow;
        await _store.UpsertAsync(MemoryScope.Project, [
            new MemoryEntry { Key = "fact:fresh", Value = "fresh", Source = "autodream", Category = "fact", CreatedAt = now, UpdatedAt = now, ExpiresAt = now.AddDays(1) },
            new MemoryEntry { Key = "fact:expired", Value = "expired", Source = "autodream", Category = "fact", CreatedAt = now.AddDays(-10), UpdatedAt = now.AddDays(-10), ExpiresAt = now.AddDays(-1) },
        ], default);

        var filtered = await _store.LoadAsync(MemoryScope.Project, default);
        filtered.Should().HaveCount(1);
        filtered[0].Key.Should().Be("fact:fresh");
    }

    [Fact]
    public async Task LoadAllAsync_IncludesExpiredEntries()
    {
        var now = DateTimeOffset.UtcNow;
        await _store.UpsertAsync(MemoryScope.Project, [
            new MemoryEntry { Key = "fact:fresh", Value = "fresh", Source = "autodream", Category = "fact", CreatedAt = now, UpdatedAt = now, ExpiresAt = now.AddDays(1) },
            new MemoryEntry { Key = "fact:expired", Value = "expired", Source = "autodream", Category = "fact", CreatedAt = now.AddDays(-10), UpdatedAt = now.AddDays(-10), ExpiresAt = now.AddDays(-1) },
        ], default);

        var all = await _store.LoadAllAsync(MemoryScope.Project, default);
        all.Should().HaveCount(2, "LoadAllAsync should include expired entries");
    }

    // Empty file / missing file

    [Fact]
    public async Task LoadAsync_ReturnsEmpty_WhenFileDoesNotExist()
    {
        var loaded = await _store.LoadAsync(MemoryScope.Project, default);
        loaded.Should().BeEmpty();
    }

    // Parse: tolerant of frontmatter

    [Fact]
    public async Task LoadAsync_HandlesFrontmatter()
    {
        var projectMemoryDir = MemdirPaths.ProjectMemoryDir(_projectDir);
        Directory.CreateDirectory(projectMemoryDir);
        var content = """
            ---
            last_updated: 2024-07-16T10:00:00Z
            entry_count: 1
            ---

            ## fact:build-command

            - source: autodream
            - category: fact
            - created_at: 2024-07-15T10:00:00Z
            - updated_at: 2024-07-16T10:00:00Z

            Build with dotnet build
            """;

        var filePath = Path.Combine(projectMemoryDir, "MEMORY.md");
        await File.WriteAllTextAsync(filePath, content);

        var loaded = await _store.LoadAsync(MemoryScope.Project, default);

        loaded.Should().HaveCount(1);
        loaded[0].Key.Should().Be("fact:build-command");
        loaded[0].Value.Should().Be("Build with dotnet build");
        loaded[0].Source.Should().Be("autodream");
        loaded[0].Category.Should().Be("fact");
    }

    // Parse: value with special characters

    [Fact]
    public async Task LoadAsync_HandlesValuesWithColons()
    {
        var now = DateTimeOffset.UtcNow;
        var entry = new MemoryEntry
        {
            Key = "fact:paths",
            Value = "Config at C:\\Users\\test\\.onecode\\settings.json",
            Source = "manual",
            Category = "manual",
            CreatedAt = now,
            UpdatedAt = now,
        };

        await _store.UpsertAsync(MemoryScope.Project, [entry], default);

        var loaded = await _store.LoadAsync(MemoryScope.Project, default);

        loaded.Should().HaveCount(1);
        loaded[0].Value.Should().Contain("C:\\Users\\test\\.onecode\\settings.json");
    }

    // Parse: Unicode / Chinese

    [Fact]
    public async Task LoadAsync_HandlesUnicodeValues()
    {
        var now = DateTimeOffset.UtcNow;
        var entry = new MemoryEntry
        {
            Key = "manual:chinese-note",
            Value = "OAuth 凭据必须用 DPAPI 加密存储",
            Source = "manual",
            Category = "manual",
            CreatedAt = now,
            UpdatedAt = now,
        };

        await _store.UpsertAsync(MemoryScope.Project, [entry], default);

        var loaded = await _store.LoadAsync(MemoryScope.Project, default);

        loaded.Should().HaveCount(1);
        loaded[0].Value.Should().Be("OAuth 凭据必须用 DPAPI 加密存储");
    }

    // Scope isolation

    [Fact]
    public async Task UserAndProjectScopesAreIsolated()
    {
        var now = DateTimeOffset.UtcNow;

        // Use the in-memory store for user scope to avoid touching the real global dir
        var inMemoryStore = new InMemoryMemoryEntryStore();
        var entry = new MemoryEntry
        {
            Key = "fact:test",
            Value = "user scope",
            Source = "manual",
            Category = "manual",
            CreatedAt = now,
            UpdatedAt = now,
        };

        await inMemoryStore.UpsertAsync(MemoryScope.User, [entry], default);
        await _store.UpsertAsync(MemoryScope.Project, [entry with { Value = "project scope" }], default);

        var userLoaded = await inMemoryStore.LoadAsync(MemoryScope.User, default);
        var projectLoaded = await _store.LoadAsync(MemoryScope.Project, default);

        userLoaded.Should().HaveCount(1);
        userLoaded[0].Value.Should().Be("user scope");
        projectLoaded.Should().HaveCount(1);
        projectLoaded[0].Value.Should().Be("project scope");
    }

    // AutoDream ApplyConsolidationChangesAsync

    [Fact]
    public async Task AutoDream_AppliesConsolidationChanges_WritesToProjectMemoryMd()
    {
        var service = new AutoDreamService(
            NullLogger<AutoDreamService>.Instance,
            NullLoggerFactory.Instance,
            agent: new AutoDreamAgentDependencies(Substitute.For<IChatClient>(), new ToolCatalog(new Lazy<List<Microsoft.Extensions.AI.AIFunction>>(() => []), new ToolMetadataRegistry(), null), Substitute.For<IModelManager>(), new PromptManager()),
            storage: new AutoDreamStorageDependencies(_store, Substitute.For<IConfigManager>(), _wdAccessor),
            globalConfigDirOverride: Path.Combine(_tempDir, "global"));

        var agentOutput = """
            [
              {
                "action": "upsert",
                "scope": "project",
                "key": "fact:build-command",
                "value": "dotnet build src/OneCode.slnx",
                "ttlHours": 2160
              },
              {
                "action": "upsert",
                "scope": "project",
                "key": "lesson:mock-httpcontext",
                "value": "Mocking IHttpContextAccessor fails silently in integration tests",
                "ttlHours": 720
              }
            ]
            """;

        var method = typeof(AutoDreamService).GetMethod(
            "ApplyConsolidationChangesAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        method.Should().NotBeNull();
        var task = (Task<int>)method!.Invoke(service, [agentOutput, default])!;
        var written = await task;

        written.Should().Be(2);

        var projectEntries = await _store.LoadAsync(MemoryScope.Project, default);
        projectEntries.Should().HaveCount(2);
        projectEntries.Should().Contain(e => e.Key == "fact:build-command");
        projectEntries.Should().Contain(e => e.Key == "lesson:mock-httpcontext");
        projectEntries.All(e => e.Source == "autodream").Should().BeTrue();
    }

    [Fact]
    public async Task AutoDream_AppliesConsolidationChanges_HandlesDeleteAction()
    {
        var service = new AutoDreamService(
            NullLogger<AutoDreamService>.Instance,
            NullLoggerFactory.Instance,
            agent: new AutoDreamAgentDependencies(Substitute.For<IChatClient>(), new ToolCatalog(new Lazy<List<Microsoft.Extensions.AI.AIFunction>>(() => []), new ToolMetadataRegistry(), null), Substitute.For<IModelManager>(), new PromptManager()),
            storage: new AutoDreamStorageDependencies(_store, Substitute.For<IConfigManager>(), _wdAccessor),
            globalConfigDirOverride: Path.Combine(_tempDir, "global"));

        var now = DateTimeOffset.UtcNow;
        await _store.UpsertAsync(MemoryScope.Project, [
            new MemoryEntry { Key = "fact:old-build", Value = "old", Source = "autodream", Category = "fact", CreatedAt = now, UpdatedAt = now },
        ], default);

        var agentOutput = """
            [
              {
                "action": "delete",
                "scope": "project",
                "key": "fact:old-build"
              }
            ]
            """;

        var method = typeof(AutoDreamService).GetMethod(
            "ApplyConsolidationChangesAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;

        var written = await (Task<int>)method.Invoke(service, [agentOutput, default])!;

        written.Should().Be(1);
        var entries = await _store.LoadAsync(MemoryScope.Project, default);
        entries.Should().BeEmpty("entry should have been deleted");
    }

    [Fact]
    public async Task AutoDream_AppliesConsolidationChanges_ToleratesMarkdownFences()
    {
        var service = new AutoDreamService(
            NullLogger<AutoDreamService>.Instance,
            NullLoggerFactory.Instance,
            agent: new AutoDreamAgentDependencies(Substitute.For<IChatClient>(), new ToolCatalog(new Lazy<List<Microsoft.Extensions.AI.AIFunction>>(() => []), new ToolMetadataRegistry(), null), Substitute.For<IModelManager>(), new PromptManager()),
            storage: new AutoDreamStorageDependencies(_store, Substitute.For<IConfigManager>(), _wdAccessor),
            globalConfigDirOverride: Path.Combine(_tempDir, "global"));

        var agentOutput = """
            Here are the consolidated memories:

            ```json
            [
              {
                "action": "upsert",
                "scope": "project",
                "key": "fact:build",
                "value": "dotnet build"
              }
            ]
            ```

            That's all.
            """;

        var method = typeof(AutoDreamService).GetMethod(
            "ApplyConsolidationChangesAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;

        var written = await (Task<int>)method.Invoke(service, [agentOutput, default])!;

        written.Should().Be(1);
        var entries = await _store.LoadAsync(MemoryScope.Project, default);
        entries.Should().HaveCount(1);
    }

    [Fact]
    public async Task AutoDream_AppliesConsolidationChanges_EmptyJsonArray_ReturnsZero()
    {
        var service = new AutoDreamService(
            NullLogger<AutoDreamService>.Instance,
            NullLoggerFactory.Instance,
            agent: new AutoDreamAgentDependencies(Substitute.For<IChatClient>(), new ToolCatalog(new Lazy<List<Microsoft.Extensions.AI.AIFunction>>(() => []), new ToolMetadataRegistry(), null), Substitute.For<IModelManager>(), new PromptManager()),
            storage: new AutoDreamStorageDependencies(_store, Substitute.For<IConfigManager>(), _wdAccessor),
            globalConfigDirOverride: Path.Combine(_tempDir, "global"));

        var method = typeof(AutoDreamService).GetMethod(
            "ApplyConsolidationChangesAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;

        var written = await (Task<int>)method.Invoke(service, ["[]", default])!;
        written.Should().Be(0);
    }

    // AutoDream prunes expired after consolidation

    [Fact]
    public async Task AutoDream_PrunesExpiredEntries_AfterConsolidation()
    {
        var service = CreateAutoDreamService();

        var past = DateTimeOffset.UtcNow.AddDays(-10);
        await _store.UpsertAsync(MemoryScope.Project, [
            new MemoryEntry { Key = "fact:old", Value = "old", Source = "autodream", Category = "fact", CreatedAt = past, UpdatedAt = past, ExpiresAt = past.AddDays(1) },
        ], default);

        var agentOutput = """
            [
              {
                "action": "upsert",
                "scope": "project",
                "key": "fact:new",
                "value": "new entry"
              }
            ]
            """;

        await InvokeApplyConsolidationAsync(service, agentOutput);

        var entries = await _store.LoadAsync(MemoryScope.Project, default);
        entries.Should().HaveCount(1, "expired entry should have been pruned");
        entries[0].Key.Should().Be("fact:new");
    }

    // AutoDream protects user-authored (manual) entries

    /// <summary>
    /// AutoDream 的输出是不可信内容：Agent 幻觉一条 delete 就能抹掉用户手写记忆。
    /// 淘汰路径已对 manual 豁免（<c>PruneAsync</c>），写入路径必须同样受保护。
    /// </summary>
    /// <remarks>
    /// 刻意使用**非 <c>manual:</c> 前缀**的 key，以便真正走到 <c>Source</c> 守卫——
    /// 用户可直接编辑 MEMORY.md 写入任意 key，前缀守卫覆盖不到这种情况。
    /// </remarks>
    [Fact]
    public async Task AutoDream_RefusesToDeleteManualEntry()
    {
        var service = CreateAutoDreamService();

        var now = DateTimeOffset.UtcNow;
        await _store.UpsertAsync(MemoryScope.Project, [
            new MemoryEntry { Key = "fact:hand-written", Value = "user wrote this", Source = "manual", Category = "manual", CreatedAt = now, UpdatedAt = now },
        ], default);

        var agentOutput = """
            [
              {
                "action": "delete",
                "scope": "project",
                "key": "fact:hand-written"
              }
            ]
            """;

        var written = await InvokeApplyConsolidationAsync(service, agentOutput);

        written.Should().Be(0, "the change was rejected, not applied");
        var entries = await _store.LoadAsync(MemoryScope.Project, default);
        entries.Should().Contain(e => e.Key == "fact:hand-written",
            "user-authored entries must survive AutoDream regardless of their key prefix");
    }

    /// <summary>
    /// 用户可能直接编辑 MEMORY.md 写入非 manual: 前缀的条目，故守卫按 <c>Source</c> 兜底。
    /// </summary>
    [Fact]
    public async Task AutoDream_RefusesToOverwriteManualEntry()
    {
        var service = CreateAutoDreamService();

        var now = DateTimeOffset.UtcNow;
        await _store.UpsertAsync(MemoryScope.Project, [
            new MemoryEntry { Key = "fact:hand-written", Value = "original", Source = "manual", Category = "manual", CreatedAt = now, UpdatedAt = now },
        ], default);

        var agentOutput = """
            [
              {
                "action": "upsert",
                "scope": "project",
                "key": "fact:hand-written",
                "value": "overwritten by autodream"
              }
            ]
            """;

        var written = await InvokeApplyConsolidationAsync(service, agentOutput);

        written.Should().Be(0);
        var entries = await _store.LoadAsync(MemoryScope.Project, default);
        entries.Single(e => e.Key == "fact:hand-written").Value.Should().Be("original");
    }

    /// <summary>
    /// <c>manual:</c> 是用户手写记忆的保留分类。AutoDream 写入的条目 Source 恒为 autodream，
    /// 若允许它创建 manual: 前缀的 key，分类与 /memory 展示会错乱。
    /// </summary>
    /// <remarks>
    /// 同时覆盖 delete 方向：前缀守卫在 <c>Source</c> 校验**之前**生效，
    /// 故对不存在的 manual: key 也应被前缀守卫拦下（而非报"目标不存在"）。
    /// </remarks>
    [Fact]
    public async Task AutoDream_SkipsManualPrefixedKey()
    {
        var service = CreateAutoDreamService();

        var agentOutput = """
            [
              {
                "action": "upsert",
                "scope": "project",
                "key": "manual:injected-by-agent",
                "value": "should never be written"
              },
              {
                "action": "delete",
                "scope": "user",
                "key": "manual:targeted-by-agent"
              }
            ]
            """;

        var written = await InvokeApplyConsolidationAsync(service, agentOutput);

        written.Should().Be(0);
        (await _store.LoadAsync(MemoryScope.Project, default)).Should().BeEmpty(
            "the reserved manual: prefix must not be creatable by AutoDream");
        (await _store.LoadAsync(MemoryScope.User, default)).Should().BeEmpty();
    }

    /// <summary>
    /// 守卫不得误伤正常路径：删除 AutoDream 自己写入的条目仍应生效。
    /// </summary>
    [Fact]
    public async Task AutoDream_StillDeletesAutoDreamOwnedEntry()
    {
        var service = CreateAutoDreamService();

        var now = DateTimeOffset.UtcNow;
        await _store.UpsertAsync(MemoryScope.Project, [
            new MemoryEntry { Key = "fact:stale", Value = "stale", Source = "autodream", Category = "fact", CreatedAt = now, UpdatedAt = now },
        ], default);

        var agentOutput = """
            [
              {
                "action": "delete",
                "scope": "project",
                "key": "fact:stale"
              }
            ]
            """;

        await InvokeApplyConsolidationAsync(service, agentOutput);

        var entries = await _store.LoadAsync(MemoryScope.Project, default);
        entries.Should().BeEmpty("autodream-owned entries remain deletable");
    }

    /// <summary>
    /// 删除不存在的 key 属 Agent 幻觉，应跳过而不是失败。
    /// </summary>
    [Fact]
    public async Task AutoDream_SkipsDeleteOfMissingKey()
    {
        var service = CreateAutoDreamService();

        var agentOutput = """
            [
              {
                "action": "delete",
                "scope": "project",
                "key": "fact:never-existed"
              }
            ]
            """;

        var written = await InvokeApplyConsolidationAsync(service, agentOutput);

        written.Should().Be(0);
        (await _store.LoadAsync(MemoryScope.Project, default)).Should().BeEmpty();
    }

    /// <summary>
    /// 反证：存储存在但读不出时，Upsert 必须拒绝提交，而不是把“读不到”当成“空库”
    /// 然后用只含新条目的内容覆盖掉原有数据。
    /// </summary>
    /// <remarks>
    /// 用目录占位 <c>MEMORY.md</c> 让读取稳定失败（Windows/Linux 行为一致），
    /// 避免依赖文件锁这类平台相关时序。
    /// </remarks>
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
    /// 内容更新不是“从未被召回”的证据：重写 value 不得把累计的命中反馈清零，
    /// 否则 AutoDream 每次整理都会静默重置淘汰排名。
    /// </summary>
    [Fact]
    public async Task UpsertAsync_ExistingEntry_PreservesUsageFeedback()
    {
        var now = DateTimeOffset.UtcNow;
        await _store.UpsertAsync(MemoryScope.Project, [
            new MemoryEntry
            {
                Key = "fact:keep-hits",
                Value = "first",
                Source = "autodream",
                Category = "fact",
                CreatedAt = now,
                UpdatedAt = now,
            },
        ], default);

        await _store.RecordHitsAsync(MemoryScope.Project, ["fact:keep-hits"], default);
        await _store.RecordHitsAsync(MemoryScope.Project, ["fact:keep-hits"], default);

        await _store.UpsertAsync(MemoryScope.Project, [
            new MemoryEntry
            {
                Key = "fact:keep-hits",
                Value = "second",
                Source = "autodream",
                Category = "fact",
                CreatedAt = now.AddHours(1),
                UpdatedAt = now.AddHours(1),
            },
        ], default);

        var loaded = await _store.LoadAsync(MemoryScope.Project, default);
        var entry = loaded.Single(e => e.Key == "fact:keep-hits");
        entry.Value.Should().Be("second");
        entry.HitCount.Should().Be(2, "a content rewrite must not reset accumulated recall feedback");
        entry.LastHitAt.Should().NotBeNull();
        entry.CreatedAt.Should().Be(now, "creation time survives updates");
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

    // Test helpers

    private AutoDreamService CreateAutoDreamService() => new(
        NullLogger<AutoDreamService>.Instance,
        NullLoggerFactory.Instance,
        agent: new AutoDreamAgentDependencies(
            Substitute.For<IChatClient>(),
            new ToolCatalog(new Lazy<List<Microsoft.Extensions.AI.AIFunction>>(() => []), new ToolMetadataRegistry(), null),
            Substitute.For<IModelManager>(),
            new PromptManager()),
        storage: new AutoDreamStorageDependencies(_store, Substitute.For<IConfigManager>(), _wdAccessor),
        globalConfigDirOverride: Path.Combine(_tempDir, "global"));

    /// <summary>调用私有 <c>ApplyConsolidationChangesAsync</c>（无公开接缝，测试经反射）。</summary>
    private static Task<int> InvokeApplyConsolidationAsync(AutoDreamService service, string agentOutput)
    {
        var method = typeof(AutoDreamService).GetMethod(
            "ApplyConsolidationChangesAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        return (Task<int>)method.Invoke(service, [agentOutput, default])!;
    }

    private sealed class TestWorkingDirectoryAccessor : IWorkingDirectoryAccessor
    {
        private readonly string _dir;
        public TestWorkingDirectoryAccessor(string dir) => _dir = dir;
        public string WorkingDirectory => _dir;
        public IDisposable BeginWorkingDirectoryChange(string newWorkingDirectory) =>
            new NoOpDisposable();
        private sealed class NoOpDisposable : IDisposable
        {
            public void Dispose() { }
        }
    }
}

/// <summary>
/// In-memory implementation of <see cref="IMemoryEntryStore"/> for testing.
/// Avoids touching the real filesystem / user global memory directory.
/// </summary>
internal sealed class InMemoryMemoryEntryStore : IMemoryEntryStore
{
    private readonly Dictionary<MemoryScope, Dictionary<string, MemoryEntry>> _data = new()
    {
        [MemoryScope.User] = new(StringComparer.OrdinalIgnoreCase),
        [MemoryScope.Project] = new(StringComparer.OrdinalIgnoreCase),
    };

    public Task<IReadOnlyList<MemoryEntry>> LoadAsync(MemoryScope scope, CancellationToken ct = default)
    {
        var entries = _data[scope].Values.Where(e => !e.IsExpired).ToList();
        return Task.FromResult<IReadOnlyList<MemoryEntry>>(entries);
    }

    public Task<IReadOnlyList<MemoryEntry>> LoadAllAsync(MemoryScope scope, CancellationToken ct = default)
    {
        var entries = _data[scope].Values.ToList();
        return Task.FromResult<IReadOnlyList<MemoryEntry>>(entries);
    }

    public Task UpsertAsync(MemoryScope scope, IEnumerable<MemoryEntry> entries, CancellationToken ct = default)
    {
        var dict = _data[scope];
        foreach (var entry in entries)
        {
            if (dict.TryGetValue(entry.Key, out var existing))
                dict[entry.Key] = entry with { CreatedAt = existing.CreatedAt };
            else
                dict[entry.Key] = entry;
        }
        return Task.CompletedTask;
    }

    public Task<bool> RemoveAsync(MemoryScope scope, string key, CancellationToken ct = default)
    {
        var removed = _data[scope].Remove(key);
        return Task.FromResult(removed);
    }

    public Task RecordHitsAsync(MemoryScope scope, IReadOnlyList<string> keys, CancellationToken ct = default)
    {
        if (keys.Count == 0)
            return Task.CompletedTask;

        var dict = _data[scope];
        var now = DateTimeOffset.UtcNow;
        foreach (var key in keys.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (dict.TryGetValue(key, out var entry))
                dict[key] = entry with { HitCount = entry.HitCount + 1, LastHitAt = now };
        }

        return Task.CompletedTask;
    }

    public Task ClearAsync(MemoryScope scope, CancellationToken ct = default)
    {
        _data[scope].Clear();
        return Task.CompletedTask;
    }

    public Task<int> PruneAsync(MemoryScope scope, CancellationToken ct = default)
    {
        var dict = _data[scope];
        var before = dict.Count;
        var expired = dict.Where(kvp => kvp.Value.IsExpired).Select(kvp => kvp.Key).ToList();
        foreach (var key in expired)
            dict.Remove(key);
        return Task.FromResult(before - dict.Count);
    }
}
