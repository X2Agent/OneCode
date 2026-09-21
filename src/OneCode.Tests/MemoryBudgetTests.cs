using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using OneCode.App.Services.Memory;
using OneCode.Core.Memory;

namespace OneCode.Tests;

/// <summary>
/// M5 行为契约：索引与检索都有总预算，且 scope 可辨。
/// </summary>
/// <remarks>
/// 条目数不是 token 上限：200 条短条目与 200 条长条目占用的上下文差异巨大，
/// 而索引是**每轮**注入的。旧实现只按条目数截断，没有任何总量控制。
/// </remarks>
public sealed class MemoryBudgetTests
{
    /// <summary>
    /// 反证核心：manual 条目没有条数上限，只有总预算能限制它们。
    /// 断言的是实际渲染出的条目行数——去掉预算后 40 条会全部出现。
    /// </summary>
    [Fact]
    public async Task LoadMemoryPromptAsync_ManyManualEntries_AreBoundedByBudget()
    {
        const int entryCount = 40;
        var store = Substitute.For<IMemoryEntryStore>();
        store.LoadAsync(MemoryScope.User, Arg.Any<CancellationToken>())
            .Returns([.. Enumerable.Range(0, entryCount).Select(i =>
                CreateEntry($"manual:long-{i}", new string('x', 2_000), source: "manual"))]);
        store.LoadAsync(MemoryScope.Project, Arg.Any<CancellationToken>()).Returns([]);
        var service = new MemoryService(NullLogger<MemoryService>.Instance, store);

        var prompt = await service.LoadMemoryPromptAsync();

        prompt.Should().NotBeNull();

        var renderedEntries = prompt!
            .Split('\n')
            .Count(line => line.StartsWith("- `manual:long-", StringComparison.Ordinal));

        renderedEntries.Should().BeGreaterThan(0, "some entries must still be shown");
        renderedEntries.Should().BeLessThan(entryCount,
            "manual entries have no count cap, so only a total budget bounds the per-turn index");
    }

    /// <summary>
    /// 反证：Project 条目必须优先于 user 条目。旧实现按 UpdatedAt 取前 8，
    /// 一个活跃的 user 级存储可以把项目自身的约定全部挤出去。
    /// </summary>
    [Fact]
    public async Task LoadMemoryPromptAsync_ProjectEntriesRankAheadOfUserEntries()
    {
        var store = Substitute.For<IMemoryEntryStore>();
        store.LoadAsync(MemoryScope.User, Arg.Any<CancellationToken>())
            .Returns([.. Enumerable.Range(0, 10).Select(i =>
                CreateEntry($"manual:user-{i}", "user level", source: "manual", updatedAt: DateTimeOffset.UtcNow))]);
        store.LoadAsync(MemoryScope.Project, Arg.Any<CancellationToken>())
            .Returns([CreateEntry("fact:project-convention", "project level", updatedAt: DateTimeOffset.UtcNow.AddDays(-30))]);
        var service = new MemoryService(NullLogger<MemoryService>.Instance, store);

        var prompt = await service.LoadMemoryPromptAsync();

        prompt.Should().Contain("project level",
            "project knowledge applies to the work at hand and must not be crowded out by older user-level entries");
    }

    /// <summary>scope 必须在索引里可辨，否则模型无法判断某条约定是否适用于当前项目。</summary>
    [Fact]
    public async Task LoadMemoryPromptAsync_AutoEntriesShowScope()
    {
        var store = Substitute.For<IMemoryEntryStore>();
        store.LoadAsync(MemoryScope.User, Arg.Any<CancellationToken>())
            .Returns([CreateEntry("fact:global", "global entry")]);
        store.LoadAsync(MemoryScope.Project, Arg.Any<CancellationToken>())
            .Returns([CreateEntry("fact:project", "project entry")]);
        var service = new MemoryService(NullLogger<MemoryService>.Instance, store);

        var prompt = await service.LoadMemoryPromptAsync();

        prompt.Should().Contain("(project)");
        prompt.Should().Contain("(global)");
    }

    /// <summary>
    /// 检索结果必须有总量上限；超出部分要被报告为省略，而不是静默丢弃或无限输出。
    /// </summary>
    [Fact]
    public async Task SearchResults_LargeEntries_AreBoundedAndReportOmissions()
    {
        var store = Substitute.For<IMemoryEntryStore>();
        store.LoadAsync(MemoryScope.User, Arg.Any<CancellationToken>()).Returns([]);
        store.LoadAsync(MemoryScope.Project, Arg.Any<CancellationToken>())
            .Returns([.. Enumerable.Range(0, 6).Select(i =>
                CreateEntry($"fact:big-{i}", "needle " + new string('y', 8_000)))]);
        var service = new MemoryService(NullLogger<MemoryService>.Instance, store);

        var matches = await service.FindRelevantMemoriesAsync("needle");

        // The service returns matches; the budget is applied when the provider formats them for the model.
        matches.Should().NotBeEmpty();

        var formatted = FormatViaProvider(matches);
        formatted.Length.Should().BeLessThan(14_000, "one search must not consume the context it informs");
        formatted.Should().Contain("omitted", "truncation must be visible, not silent");
    }

    private static string FormatViaProvider(IReadOnlyList<MemoryEntryMatch> matches)
    {
        // Exercise the same formatting the tool uses, without standing up MAF.
        var formatter = typeof(MemorySearchProviderFactory).GetMethod(
            "FormatResults",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;

        var results = matches.Select(match => new Microsoft.Agents.AI.TextSearchProvider.TextSearchResult
        {
            SourceName = match.Entry.Key,
            Text = match.Entry.Value.Trim(),
            RawRepresentation = match,
        }).ToList();

        return (string)formatter.Invoke(null, [results])!;
    }

    private static MemoryEntry CreateEntry(
        string key,
        string value,
        string source = "autodream",
        DateTimeOffset? updatedAt = null)
    {
        var now = updatedAt ?? DateTimeOffset.UtcNow;
        return new MemoryEntry
        {
            Key = key,
            Value = value,
            Source = source,
            Category = MemoryEntry.DeriveCategory(key),
            CreatedAt = now,
            UpdatedAt = now,
        };
    }
}
