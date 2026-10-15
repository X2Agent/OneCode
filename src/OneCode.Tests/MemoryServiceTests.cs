using Microsoft.Extensions.Logging.Abstractions;
using OneCode.App.Services.Memory;
using OneCode.Core.Memory;

namespace OneCode.Tests;

public sealed class MemoryServiceTests
{
    [Fact]
    public async Task LoadMemoryPromptAsync_StripsInternalMetadata_AndInjectsFullBody()
    {
        var store = new InMemoryMemoryEntryStore();
        var now = DateTimeOffset.UtcNow;
        await store.UpsertAsync(MemoryScope.Project,
        [
            new MemoryEntry
            {
                Key = "fact:build-command",
                Value = "dotnet build src/OneCode.slnx\ndotnet test src/OneCode.slnx",
                Source = "autodream",
                Category = "fact",
                CreatedAt = now,
                UpdatedAt = now,
                ExpiresAt = now.AddDays(30),
            },
        ]);

        var service = new MemoryService(NullLogger<MemoryService>.Instance, store);

        var prompt = await service.LoadMemoryPromptAsync();

        prompt.Should().NotBeNull();
        prompt.Should().Contain("- `fact:build-command` `[fact]` (project) — dotnet build src/OneCode.slnx\ndotnet test src/OneCode.slnx");
        prompt.Should().NotContain("updated_at");
        prompt.Should().NotContain("expires_at");
        prompt.Should().NotContain("source:");
    }

    [Fact]
    public async Task ListMemoryEntriesAsync_ReturnsProjectEntriesBeforeGlobalEntries()
    {
        var store = new InMemoryMemoryEntryStore();
        var now = DateTimeOffset.UtcNow;
        await store.UpsertAsync(MemoryScope.User,
        [
            new MemoryEntry
            {
                Key = "manual:user-pref",
                Value = "prefer concise answers",
                Source = "manual",
                Category = "manual",
                CreatedAt = now,
                UpdatedAt = now,
            },
        ]);
        await store.UpsertAsync(MemoryScope.Project,
        [
            new MemoryEntry
            {
                Key = "fact:project-cmd",
                Value = "dotnet build",
                Source = "autodream",
                Category = "fact",
                CreatedAt = now,
                UpdatedAt = now,
            },
        ]);

        var service = new MemoryService(NullLogger<MemoryService>.Instance, store);

        var list = await service.ListMemoryEntriesAsync();

        list.Should().HaveCount(2);
        list[0].ScopeLabel.Should().Be("project");
        list[0].Entry.Key.Should().Be("fact:project-cmd");
        list[1].ScopeLabel.Should().Be("global");
        list[1].Entry.Key.Should().Be("manual:user-pref");
    }
}
