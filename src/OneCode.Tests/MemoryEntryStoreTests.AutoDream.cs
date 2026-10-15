// MemoryEntryStoreTests partial：AutoDream 整合写入与 manual 条目守卫（原文件按职责区块的纯文本搬移）
using OneCode.Core.Memory;
namespace OneCode.Tests;

public sealed partial class MemoryEntryStoreTests
{
    [Fact]
    public async Task AutoDream_AppliesConsolidationChanges_WritesToProjectMemoryMd()
    {
        var applier = CreateConsolidationApplier();

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

        var written = await applier.ApplyConsolidationChangesAsync(agentOutput, default);

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
        var applier = CreateConsolidationApplier();

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

        var written = await applier.ApplyConsolidationChangesAsync(agentOutput, default);

        written.Should().Be(1);
        var entries = await _store.LoadAsync(MemoryScope.Project, default);
        entries.Should().BeEmpty("entry should have been deleted");
    }

    [Fact]
    public async Task AutoDream_AppliesConsolidationChanges_ToleratesMarkdownFences()
    {
        var applier = CreateConsolidationApplier();

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

        var written = await applier.ApplyConsolidationChangesAsync(agentOutput, default);

        written.Should().Be(1);
        var entries = await _store.LoadAsync(MemoryScope.Project, default);
        entries.Should().HaveCount(1);
    }

    [Fact]
    public async Task AutoDream_AppliesConsolidationChanges_EmptyJsonArray_ReturnsZero()
    {
        var applier = CreateConsolidationApplier();

        var written = await applier.ApplyConsolidationChangesAsync("[]", default);
        written.Should().Be(0);
    }

    // AutoDream prunes expired after consolidation

    [Fact]
    public async Task AutoDream_PrunesExpiredEntries_AfterConsolidation()
    {
        var applier = CreateConsolidationApplier();

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

        await InvokeApplyConsolidationAsync(applier, agentOutput);

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
        var applier = CreateConsolidationApplier();

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

        var written = await InvokeApplyConsolidationAsync(applier, agentOutput);

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
        var applier = CreateConsolidationApplier();

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

        var written = await InvokeApplyConsolidationAsync(applier, agentOutput);

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
        var applier = CreateConsolidationApplier();

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

        var written = await InvokeApplyConsolidationAsync(applier, agentOutput);

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
        var applier = CreateConsolidationApplier();

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

        await InvokeApplyConsolidationAsync(applier, agentOutput);

        var entries = await _store.LoadAsync(MemoryScope.Project, default);
        entries.Should().BeEmpty("autodream-owned entries remain deletable");
    }

    /// <summary>
    /// 删除不存在的 key 属 Agent 幻觉，应跳过而不是失败。
    /// </summary>
    [Fact]
    public async Task AutoDream_SkipsDeleteOfMissingKey()
    {
        var applier = CreateConsolidationApplier();

        var agentOutput = """
            [
              {
                "action": "delete",
                "scope": "project",
                "key": "fact:never-existed"
              }
            ]
            """;

        var written = await InvokeApplyConsolidationAsync(applier, agentOutput);

        written.Should().Be(0);
        (await _store.LoadAsync(MemoryScope.Project, default)).Should().BeEmpty();
    }

    /// <summary>
    /// 反证：当 Agent 输出省略 ttlHours 时，必须自动赋予 720h（30 天）默认过期时间，
    /// 绝不能变成 ExpiresAt = null（永不过期）。
    /// </summary>
    [Fact]
    public async Task AutoDream_OmittedTtlHours_DefaultsTo30DaysInsteadOfNeverExpiring()
    {
        var applier = CreateConsolidationApplier();
        var before = DateTimeOffset.UtcNow;

        var agentOutput = """
            [
              {
                "action": "upsert",
                "scope": "project",
                "key": "lesson:omitted-ttl",
                "value": "default ttl must apply"
              }
            ]
            """;

        var written = await InvokeApplyConsolidationAsync(applier, agentOutput);

        written.Should().Be(1);
        var entries = await _store.LoadAsync(MemoryScope.Project, default);
        entries.Should().ContainSingle();
        entries[0].ExpiresAt.Should().NotBeNull("omitted ttlHours must default to 720h, not null");
        entries[0].ExpiresAt!.Value.Should().BeCloseTo(before.AddHours(720), TimeSpan.FromMinutes(1));
    }

    /// <summary>
    /// 当 upsert 携带 supersedes 时，原子替换并删除旧的 autodream 条目，但拒绝删除 manual 条目。
    /// </summary>
    [Fact]
    public async Task AutoDream_UpsertWithSupersedes_RemovesOldAutoDreamKeysAndProtectsManualKeys()
    {
        var applier = CreateConsolidationApplier();
        var now = DateTimeOffset.UtcNow;

        await _store.UpsertAsync(MemoryScope.Project, [
            new MemoryEntry { Key = "lesson:old-a", Value = "fragment A", Source = "autodream", Category = "lesson", CreatedAt = now, UpdatedAt = now },
            new MemoryEntry { Key = "lesson:old-b", Value = "fragment B", Source = "autodream", Category = "lesson", CreatedAt = now, UpdatedAt = now },
            new MemoryEntry { Key = "fact:manual-protected", Value = "user wrote this", Source = "manual", Category = "manual", CreatedAt = now, UpdatedAt = now },
        ], default);

        var agentOutput = """
            [
              {
                "action": "upsert",
                "scope": "project",
                "key": "lesson:merged-ab",
                "value": "consolidated A and B",
                "supersedes": ["lesson:old-a", "lesson:old-b", "fact:manual-protected"]
              }
            ]
            """;

        var written = await InvokeApplyConsolidationAsync(applier, agentOutput);

        written.Should().Be(1);
        var entries = await _store.LoadAsync(MemoryScope.Project, default);
        entries.Select(e => e.Key).Should().BeEquivalentTo(["lesson:merged-ab", "fact:manual-protected"]);
    }

}








