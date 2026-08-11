using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using OneCode.App.Services.AutoDream;
using OneCode.App.Services.Memory;
using OneCode.App.Tools;
using OneCode.Core.Memory;
using OneCode.Core.Models;
using OneCode.Core.Prompt;
using OneCode.Core.Tools;
using OneCode.Infrastructure.Config;
using System.Diagnostics;

namespace OneCode.Tests;

/// <summary>
/// Tests for <see cref="MemoryEntryStore"/>: MEMORY.md parsing, serialization, upsert,
/// remove, prune (TTL expiry + LRU eviction).
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

    // Prune: LRU eviction

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
        var service = new AutoDreamService(
            NullLogger<AutoDreamService>.Instance,
            NullLoggerFactory.Instance,
            agent: new AutoDreamAgentDependencies(Substitute.For<IChatClient>(), new ToolCatalog(new Lazy<List<Microsoft.Extensions.AI.AIFunction>>(() => []), new ToolMetadataRegistry(), null), Substitute.For<IModelManager>(), new PromptManager()),
            storage: new AutoDreamStorageDependencies(_store, Substitute.For<IConfigManager>(), _wdAccessor),
            globalConfigDirOverride: Path.Combine(_tempDir, "global"));

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

        var method = typeof(AutoDreamService).GetMethod(
            "ApplyConsolidationChangesAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;

        await (Task<int>)method.Invoke(service, [agentOutput, default])!;

        var entries = await _store.LoadAsync(MemoryScope.Project, default);
        entries.Should().HaveCount(1, "expired entry should have been pruned");
        entries[0].Key.Should().Be("fact:new");
    }

    // Test helpers

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
