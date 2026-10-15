using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using OneCode.App.Services.Memory;
using OneCode.Core.Memory;

namespace OneCode.Tests;

/// <summary>
/// 记忆上下文注入总预算与优先级契约：
/// 总字符受 <see cref="MemoryService.MaxMemoryContextChars"/> 限制，
/// 排序遵循 manual 优先 → Project 优先于 User → UpdatedAt 降序，且标注作用域。
/// </summary>
public sealed class MemoryBudgetTests
{
    /// <summary>
    /// 反证核心：manual 条目没有条数上限，只有总字符预算（8000 字符）能限制它们。
    /// 断言的是实际渲染出的条目数与总长度——去掉预算后 40 条会全部出现。
    /// </summary>
    [Fact]
    public async Task LoadMemoryPromptAsync_ManyManualEntries_AreBoundedByBudget()
    {
        const int entryCount = 40;
        var store = Substitute.For<IMemoryEntryStore>();
        store.LoadAsync(MemoryScope.User, Arg.Any<CancellationToken>())
            .Returns([.. Enumerable.Range(0, entryCount).Select(i =>
                CreateEntry($"manual:long-{i}", new string('x', 1_500), source: "manual"))]);
        store.LoadAsync(MemoryScope.Project, Arg.Any<CancellationToken>()).Returns([]);
        var service = new MemoryService(NullLogger<MemoryService>.Instance, store);

        var prompt = await service.LoadMemoryPromptAsync();

        prompt.Should().NotBeNull();
        prompt!.Length.Should().BeLessThanOrEqualTo(MemoryService.MaxMemoryContextChars);

        var renderedEntries = prompt
            .Split('\n')
            .Count(line => line.Contains("`manual:long-", StringComparison.Ordinal));

        renderedEntries.Should().BeInRange(1, 6,
            "8000-char budget fits ~5 entries of 1500 chars each, bounding total context injection");
    }

    /// <summary>
    /// 反证：Project 条目必须优先于 User 条目（无论在 manual 还是 auto 分区）。
    /// </summary>
    [Fact]
    public async Task LoadMemoryPromptAsync_ProjectEntriesRankAheadOfUserEntries()
    {
        var store = Substitute.For<IMemoryEntryStore>();
        store.LoadAsync(MemoryScope.User, Arg.Any<CancellationToken>())
            .Returns([
                CreateEntry("manual:user-rule", "user manual rule", source: "manual", updatedAt: DateTimeOffset.UtcNow),
                CreateEntry("fact:user-fact", "user auto fact", updatedAt: DateTimeOffset.UtcNow),
            ]);
        store.LoadAsync(MemoryScope.Project, Arg.Any<CancellationToken>())
            .Returns([
                CreateEntry("manual:project-rule", "project manual rule", source: "manual", updatedAt: DateTimeOffset.UtcNow.AddDays(-30)),
                CreateEntry("fact:project-convention", "project auto level", updatedAt: DateTimeOffset.UtcNow.AddDays(-30)),
            ]);
        var service = new MemoryService(NullLogger<MemoryService>.Instance, store);

        var prompt = await service.LoadMemoryPromptAsync();

        prompt.Should().NotBeNull();
        prompt!.IndexOf("project manual rule", StringComparison.Ordinal)
            .Should().BeLessThan(prompt.IndexOf("user manual rule", StringComparison.Ordinal),
                "project manual entries must precede user manual entries even when older");
        prompt.IndexOf("project auto level", StringComparison.Ordinal)
            .Should().BeLessThan(prompt.IndexOf("user auto fact", StringComparison.Ordinal),
                "project auto entries must precede user auto entries even when older");
    }

    /// <summary>scope 必须在手动和自动记忆里都可辨，且多行完整正文原样注入。</summary>
    [Fact]
    public async Task LoadMemoryPromptAsync_ShowsScopeAndFullMultiLineBody()
    {
        var store = Substitute.For<IMemoryEntryStore>();
        store.LoadAsync(MemoryScope.User, Arg.Any<CancellationToken>())
            .Returns([CreateEntry("manual:global-pref", "Global line 1\nGlobal line 2", source: "manual")]);
        store.LoadAsync(MemoryScope.Project, Arg.Any<CancellationToken>())
            .Returns([CreateEntry("lesson:project-gotcha", "Project line 1\nProject line 2")]);
        var service = new MemoryService(NullLogger<MemoryService>.Instance, store);

        var prompt = await service.LoadMemoryPromptAsync();

        prompt.Should().Contain("`manual:global-pref` (global)");
        prompt.Should().Contain("Global line 1\nGlobal line 2");
        prompt.Should().Contain("`lesson:project-gotcha` `[lesson]` (project)");
        prompt.Should().Contain("Project line 1\nProject line 2");
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
