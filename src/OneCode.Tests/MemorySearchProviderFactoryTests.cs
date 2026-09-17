using Microsoft.Agents.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using OneCode.App.Services.Memory;
using OneCode.Core.Memory;

namespace OneCode.Tests;

/// <summary>
/// <c>search_memories</c> 的检索委托契约——命中回写接线与失败降级。
/// </summary>
/// <remarks>
/// <para>
/// 检索/工具暴露/结果格式化由 MAF <see cref="TextSearchProvider"/> 提供，不属于被测范围。
/// 本文件只覆盖 OneCode 自己写的那一小段：<see cref="MemorySearchProviderFactory.SearchAsync"/>
/// 的检索调用、命中回写（<c>RecordHitsAsync</c>）与失败降级。
/// </para>
/// <para>
/// <b>为何必须测「命中回写」</b>：淘汰策略（ADR 0004 §11.2）依赖 <c>HitCount</c>。若回写接线
/// 断了，淘汰会静默退化为「只按时间」，且不会报错——只有盘上数据事后才能看出。
/// <c>MemoryEntryStoreTests</c> 只覆盖 store 层，本文件覆盖**接线本身**。
/// </para>
/// </remarks>
public sealed class MemorySearchProviderFactoryTests
{
    private static readonly ILogger Logger = NullLogger.Instance;

    [Fact]
    public async Task SearchAsync_ReturnsMatchesWithFullValue_AndMetadataIntact()
    {
        var entry = CreateEntry("fact:build-command", "Build with dotnet build.");
        var memoryService = CreateService(((entry, MemoryScope.Project, 9)));
        using var cts = new CancellationTokenSource();

        var results = (await MemorySearchProviderFactory.SearchAsync(
            memoryService, Logger, "build", cts.Token)).ToList();

        results.Should().ContainSingle();
        // 完整 Value 交给 LLM（摘要索引只给首行，这里必须给全文）
        results[0].Text.Should().Be("Build with dotnet build.");
        // SourceName 用 key，便于模型引用
        results[0].SourceName.Should().Be("fact:build-command");
        // RawRepresentation 携带 scope/score，供 FormatResults 渲染
        var match = results[0].RawRepresentation.Should().BeOfType<MemoryEntryMatch>().Subject;
        match.Scope.Should().Be(MemoryScope.Project);
        match.RelevanceScore.Should().Be(9);
    }

    /// <summary>
    /// 核心接线：显式检索命中的条目必须回写使用反馈，否则淘汰拿不到 HitCount。
    /// 断言的是**被测代码产生的回写调用及其参数**（scope + keys），而非 Mock 自身的功能。
    /// </summary>
    [Fact]
    public async Task SearchAsync_RecalledEntries_ReportsHitsGroupedByScope()
    {
        var projectEntry = CreateEntry("fact:proj", "project fact");
        var userEntry = CreateEntry("manual:glob", "user fact");
        var memoryService = CreateService(
            (projectEntry, MemoryScope.Project, 8),
            (userEntry, MemoryScope.User, 4));
        using var cts = new CancellationTokenSource();

        await MemorySearchProviderFactory.SearchAsync(memoryService, Logger, "fact", cts.Token);

        // 回写必须按 scope 分组：store 的 RecordHitsAsync 以 scope 定位 MEMORY.md
        await memoryService.Received(1).RecordHitsAsync(
            MemoryScope.Project, Arg.Is<IReadOnlyList<string>>(k => k.SequenceEqual(new[] { "fact:proj" })),
            Arg.Any<CancellationToken>());
        await memoryService.Received(1).RecordHitsAsync(
            MemoryScope.User, Arg.Is<IReadOnlyList<string>>(k => k.SequenceEqual(new[] { "manual:glob" })),
            Arg.Any<CancellationToken>());
        // 共两次：每个 scope 一次，不得交叉或重复
        await memoryService.Received(2).RecordHitsAsync(
            Arg.Any<MemoryScope>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());
    }

    /// <summary>无命中时不得产生任何回写调用（空 keys 写盘是纯浪费）。</summary>
    [Fact]
    public async Task SearchAsync_NoMatches_DoesNotRecordHits()
    {
        var memoryService = CreateService();

        var results = (await MemorySearchProviderFactory.SearchAsync(
            memoryService, Logger, "nothing matches", TestContext.Current.CancellationToken)).ToList();

        results.Should().BeEmpty();
        await memoryService.DidNotReceiveWithAnyArgs()
            .RecordHitsAsync(default, default!, default);
    }

    /// <summary>空 query 直接返回提示，不触发检索（避免无意义的全量扫描与回写）。</summary>
    [Fact]
    public async Task SearchAsync_EmptyQuery_ShortCircuitsWithoutSearching()
    {
        var memoryService = CreateService();

        var results = (await MemorySearchProviderFactory.SearchAsync(
            memoryService, Logger, "   ", TestContext.Current.CancellationToken)).ToList();

        results.Should().ContainSingle();
        results[0].Text.Should().Contain("empty query");
        await memoryService.DidNotReceiveWithAnyArgs()
            .FindRelevantMemoriesAsync(default!, default);
    }

    /// <summary>
    /// 记忆文件损坏时检索抛错，工具调用不得随之失败——降级为提示文本。
    /// 这是「记忆坏了不影响主流程」这一产品约束的守卫。
    /// </summary>
    [Fact]
    public async Task SearchAsync_SearchThrows_DegradesToMessageInsteadOfPropagating()
    {
        var memoryService = Substitute.For<IMemoryService>();
        memoryService.FindRelevantMemoriesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<MemoryEntryMatch>>(_ => throw new IOException("MEMORY.md is corrupt"));

        var results = (await MemorySearchProviderFactory.SearchAsync(
            memoryService, Logger, "anything", TestContext.Current.CancellationToken)).ToList();

        results.Should().ContainSingle();
        results[0].Text.Should().Contain("Memory search failed");
        results[0].Text.Should().Contain("MEMORY.md is corrupt");
    }

    /// <summary>
    /// 命中回写是 best-effort：写盘失败**不得**让已经成功的检索失败。
    /// 否则一个只读磁盘会让整个 search_memories 工具不可用。
    /// </summary>
    [Fact]
    public async Task SearchAsync_HitRecordingThrows_StillReturnsSearchResults()
    {
        var entry = CreateEntry("fact:still-returned", "content survives");
        var memoryService = CreateService((entry, MemoryScope.Project, 5));
        memoryService.RecordHitsAsync(Arg.Any<MemoryScope>(), Arg.Any<IReadOnlyList<string>>(),
                Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException(new IOException("disk is read-only")));

        var results = (await MemorySearchProviderFactory.SearchAsync(
            memoryService, Logger, "survives", TestContext.Current.CancellationToken)).ToList();

        results.Should().ContainSingle("hit-recording failures must not fail the search itself");
        results[0].Text.Should().Be("content survives");
    }

    // 辅助

    private static MemoryEntry CreateEntry(string key, string value) => new()
    {
        Key = key,
        Value = value,
        Source = key.StartsWith("manual:", StringComparison.OrdinalIgnoreCase) ? "manual" : "autodream",
        Category = MemoryEntry.DeriveCategory(key),
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
    };

    private static IMemoryService CreateService(params (MemoryEntry Entry, MemoryScope Scope, int Score)[] matches)
    {
        var service = Substitute.For<IMemoryService>();
        service.FindRelevantMemoriesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(matches.Select(m => new MemoryEntryMatch(m.Entry, m.Scope, m.Score)).ToList());
        return service;
    }
}
