using Microsoft.Extensions.Logging.Abstractions;
using OneCode.App.Services.AutoDream;
using OneCode.Infrastructure.Agent;

namespace OneCode.Tests;

/// <summary>
/// M4 行为契约：AutoDream 的候选输入必须来自会话工作产物。
/// </summary>
/// <remarks>
/// 会话事件只记录「发生过一次对话」，工作产物才是 Agent 自己写下的发现与决策。
/// 只读事件会迫使整合器重新推导 Agent 已经得出的结论，且无法区分哪些结论 Agent 认为值得保留。
/// </remarks>
public sealed class AutoDreamWorkingMemoryScannerTests : IDisposable
{
    private readonly string _projectDir;
    private readonly string _memoryRoot;
    private readonly AutoDreamWorkingMemoryScanner _scanner;

    public AutoDreamWorkingMemoryScannerTests()
    {
        _projectDir = Path.Combine(Path.GetTempPath(), $"AutoDreamWm_{Guid.NewGuid():N}");
        _memoryRoot = FileMemoryStorePaths.ResolveRoot(_projectDir);
        Directory.CreateDirectory(_memoryRoot);
        _scanner = new AutoDreamWorkingMemoryScanner(
            NullLogger<AutoDreamWorkingMemoryScanner>.Instance);
    }

    public void Dispose()
    {
        try { Directory.Delete(_projectDir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void Collect_ReturnsArtefactsWrittenSinceLastConsolidation()
    {
        WriteArtefact("findings.md", "the parser drops trailing commas", modifiedAt: DateTimeOffset.UtcNow);

        var artefacts = _scanner.Collect(_projectDir, DateTimeOffset.UtcNow.AddHours(-1), maxArtefacts: 10);

        artefacts.Should().ContainSingle();
        artefacts[0].RelativePath.Should().Be("findings.md");
        artefacts[0].Content.Should().Contain("trailing commas");
    }

    /// <summary>
    /// 反证：早于上次整合的产物不得重复进入候选集，否则每次整合都会重新处理同一批笔记。
    /// </summary>
    [Fact]
    public void Collect_ArtefactsOlderThanSince_AreExcluded()
    {
        WriteArtefact("stale.md", "already consolidated", modifiedAt: DateTimeOffset.UtcNow.AddDays(-2));

        var artefacts = _scanner.Collect(_projectDir, DateTimeOffset.UtcNow.AddDays(-1), maxArtefacts: 10);

        artefacts.Should().BeEmpty();
    }

    /// <summary>
    /// 快照必须携带路径与时间戳，否则无法把候选归属回它来自哪个修订。
    /// </summary>
    [Fact]
    public void Collect_SnapshotCarriesPathAndTimestamp()
    {
        var modified = DateTimeOffset.UtcNow.AddMinutes(-5);
        WriteArtefact("notes/decision.md", "chose SQLite over LiteDB", modified);

        var artefact = _scanner.Collect(_projectDir, DateTimeOffset.UtcNow.AddHours(-1), 10).Single();

        artefact.RelativePath.Should().Be(Path.Combine("notes", "decision.md"));
        artefact.LastModifiedAt.Should().BeCloseTo(modified, TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void Collect_EmptyFiles_AreSkipped()
    {
        WriteArtefact("empty.md", "   ", modifiedAt: DateTimeOffset.UtcNow);

        _scanner.Collect(_projectDir, DateTimeOffset.UtcNow.AddHours(-1), 10).Should().BeEmpty();
    }

    /// <summary>
    /// 产物数量无界，提示词不是：超出上限时必须截断而不是把整棵树塞进 prompt。
    /// </summary>
    [Fact]
    public void Collect_MoreThanMaxArtefacts_ReturnsNewestFirst()
    {
        for (var i = 0; i < 5; i++)
            WriteArtefact($"note{i}.md", $"content {i}", DateTimeOffset.UtcNow.AddMinutes(-i));

        var artefacts = _scanner.Collect(_projectDir, DateTimeOffset.UtcNow.AddHours(-1), maxArtefacts: 2);

        artefacts.Should().HaveCount(2);
        artefacts[0].RelativePath.Should().Be("note0.md", "newest artefacts are the most relevant candidates");
        artefacts[1].RelativePath.Should().Be("note1.md");
    }

    /// <summary>没有工作产物时返回空集，让调用方走事件回退，而不是抛错中断整合。</summary>
    [Fact]
    public void Collect_NoWorkingMemoryDirectory_ReturnsEmpty()
    {
        var other = Path.Combine(Path.GetTempPath(), $"AutoDreamWm_none_{Guid.NewGuid():N}");

        _scanner.Collect(other, DateTimeOffset.UtcNow.AddDays(-1), 10).Should().BeEmpty();
    }

    private void WriteArtefact(string relativePath, string content, DateTimeOffset modifiedAt)
    {
        var path = Path.Combine(_memoryRoot, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        File.SetLastWriteTimeUtc(path, modifiedAt.UtcDateTime);
    }
}
