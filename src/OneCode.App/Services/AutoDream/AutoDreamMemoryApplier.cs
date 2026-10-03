using OneCode.Core.Memory;

namespace OneCode.App.Services.AutoDream;

/// <summary>
/// AutoDream 的记忆条目写入器：解析整合 Agent 输出的增量变更 JSON 数组，
/// 按作用域合并写入对应的 MEMORY.md（经 <see cref="IMemoryEntryStore"/>），并在写入后清理过期条目。
/// </summary>
/// <remarks>
/// Agent 输出为不可信内容：Key / Value 必须经 <see cref="AutoDreamOutputSanitizer"/> 清洗；
/// manual 条目是用户显式意图，不可被 AutoDream 创建、覆盖或删除（安全闸见 <see cref="ApplyConsolidationChangesAsync"/>）。
/// </remarks>
internal sealed class AutoDreamMemoryApplier(
    IMemoryEntryStore entryStore,
    ILogger logger,
    Func<string?> projectRootProvider)
{
    private readonly IMemoryEntryStore _entryStore = entryStore;
    private readonly ILogger _logger = logger;
    private readonly Func<string?> _projectRootProvider = projectRootProvider;

    /// <summary>JSON 反序列化选项：camelCase 属性名大小写不敏感，容忍 Agent 输出风格差异。</summary>
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>用户手写条目的来源标记；这些条目是用户显式意图，AutoDream 不得删除或改写。</summary>
    private static readonly string ManualSource = "manual";

    /// <summary>用户手写条目的 key 前缀；AutoDream 也不得以该前缀创建条目（否则分类语义混乱）。</summary>
    private const string ManualKeyPrefix = "manual:";

    /// <summary>
    /// 解析 Agent 输出的增量变更 JSON 数组，合并写入对应的 MEMORY.md 文件。
    /// 容错：容忍 Agent 在 JSON 前后添加 markdown 围栏或额外说明文本。
    /// 安全：Agent 输出为不可信内容，必须经过 <see cref="AutoDreamOutputSanitizer.SanitizeKey"/> / <see cref="AutoDreamOutputSanitizer.SanitizeValue"/>
    /// 清洗，防止 MEMORY.md 结构注入（如 <c>## </c> 开头的行会被 <c>OneCode.Infrastructure.Memory.MemoryEntryStore.ParseEntries</c>
    /// 误识别为新的 entry header，导致条目边界错乱、内容串入相邻条目）。
    /// </summary>
    public async Task<int> ApplyConsolidationChangesAsync(string outputText, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(outputText))
            return 0;

        var json = AutoDreamOutputSanitizer.ExtractJsonArray(outputText);
        if (json is null)
        {
            _logger.LogWarning("AutoDream: failed to extract JSON array from agent output");
            return 0;
        }

        List<ConsolidationChange>? changes;
        try
        {
            changes = JsonSerializer.Deserialize<List<ConsolidationChange>>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "AutoDream: failed to parse consolidation changes JSON");
            return 0;
        }

        if (changes is null || changes.Count == 0)
            return 0;

        // 配额限制：防止 Agent 输出过多变更导致 MEMORY.md 膨胀或写入风暴。
        // 50 条覆盖典型整合场景（事实/约定/教训/纠正），超出部分丢弃并记录。
        if (changes.Count > AutoDreamOutputSanitizer.MaxChangesPerConsolidation)
        {
            _logger.LogWarning(
                "AutoDream: agent output {Count} changes exceeds limit {Max}, truncating",
                changes.Count, AutoDreamOutputSanitizer.MaxChangesPerConsolidation);
            changes = changes.Take(AutoDreamOutputSanitizer.MaxChangesPerConsolidation).ToList();
        }

        var now = DateTimeOffset.UtcNow;
        var projectRoot = _projectRootProvider();

        // 载入现有条目（key → 条目），用于删除/覆盖前校验，保护用户手写记忆。
        // PruneAsync 已对 manual 做淘汰豁免，此处是同一原则在写入路径上的落实。
        // 用 LoadAllAsync（含过期）：过期的手写条目也须受保护，不能被 Agent 顺手清掉。
        var userExisting = (await _entryStore.LoadAllAsync(MemoryScope.User, ct).ConfigureAwait(false))
            .ToDictionary(e => e.Key, StringComparer.OrdinalIgnoreCase);
        var projectExisting = string.IsNullOrWhiteSpace(projectRoot)
            ? new Dictionary<string, MemoryEntry>(StringComparer.OrdinalIgnoreCase)
            : (await _entryStore.LoadAllAsync(MemoryScope.Project, ct).ConfigureAwait(false))
                .ToDictionary(e => e.Key, StringComparer.OrdinalIgnoreCase);

        // Group by scope to batch-write each MEMORY.md
        List<MemoryEntry> userEntries = [];
        List<MemoryEntry> projectEntries = [];
        List<string> userDeleteKeys = [];
        List<string> projectDeleteKeys = [];
        var written = 0;
        var skipped = 0;

        foreach (var change in changes)
        {
            // 清洗 Key：strip 换行/前导#防止 entry header 注入，限制长度
            var key = AutoDreamOutputSanitizer.SanitizeKey(change.Key);
            if (string.IsNullOrEmpty(key))
            {
                skipped++;
                continue;
            }

            // 安全闸：禁止使用 manual: 前缀。manual 是用户手写记忆的保留分类，
            // AutoDream 写入的条目 Source 恒为 "autodream"，混用会让分类与 /memory 展示错乱。
            if (key.StartsWith(ManualKeyPrefix, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning(
                    "AutoDream: change with key '{Key}' uses the reserved '{Prefix}' prefix, skipping",
                    key, ManualKeyPrefix);
                skipped++;
                continue;
            }

            // 显式 scope 校验：仅接受 "user"/"project"，其他值跳过。
            if (!string.Equals(change.Scope, "user", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(change.Scope, "project", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning("AutoDream: change with key '{Key}' has invalid scope '{Scope}', skipping",
                    key, change.Scope);
                skipped++;
                continue;
            }

            var scope = string.Equals(change.Scope, "user", StringComparison.OrdinalIgnoreCase)
                ? MemoryScope.User
                : MemoryScope.Project;
            var existing = scope == MemoryScope.User ? userExisting : projectExisting;

            if (string.Equals(change.Action, "delete", StringComparison.OrdinalIgnoreCase))
            {
                // 安全闸：只允许删除 AutoDream 自己写入的条目。用户手写的 manual 条目是显式意图，
                // 与 PruneAsync 的淘汰豁免一致——否则 Agent 幻觉一条 delete 就能抹掉用户记忆。
                if (!existing.TryGetValue(key, out var deleteTarget))
                {
                    _logger.LogWarning(
                        "AutoDream: delete target '{Key}' not found in {Scope} scope, skipping",
                        key, change.Scope);
                    skipped++;
                    continue;
                }

                if (string.Equals(deleteTarget.Source, ManualSource, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogWarning(
                        "AutoDream: refusing to delete user-authored entry '{Key}' (source={Source})",
                        key, deleteTarget.Source);
                    skipped++;
                    continue;
                }

                if (scope == MemoryScope.User)
                    userDeleteKeys.Add(key);
                else
                    projectDeleteKeys.Add(key);
                written++;
                continue;
            }

            // 非 delete action 仅接受 upsert（默认）。其他 action 值跳过。
            if (!string.IsNullOrEmpty(change.Action)
                && !string.Equals(change.Action, "upsert", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning("AutoDream: change with key '{Key}' has unknown action '{Action}', skipping",
                    key, change.Action);
                skipped++;
                continue;
            }

            // 清洗 Value：strip 行首 ## 防止 entry header 注入，限制长度
            var value = AutoDreamOutputSanitizer.SanitizeValue(change.Value);
            if (string.IsNullOrEmpty(value))
            {
                skipped++;
                continue;
            }

            // 安全闸：不得覆盖用户手写条目。manual 前缀已在上面拒绝，
            // 但用户也可能直接编辑 MEMORY.md 写入其他 key——按 Source 兜底。
            if (existing.TryGetValue(key, out var current)
                && string.Equals(current.Source, ManualSource, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning(
                    "AutoDream: refusing to overwrite user-authored entry '{Key}' (source={Source})",
                    key, current.Source);
                skipped++;
                continue;
            }

            var category = MemoryEntry.DeriveCategory(key);
            TimeSpan? ttl = change.TtlHours is > 0
                ? TimeSpan.FromHours(change.TtlHours.Value)
                : null;

            var entry = new MemoryEntry
            {
                Key = key,
                Value = value,
                Source = "autodream",
                Category = category,
                CreatedAt = now,
                UpdatedAt = now,
                ExpiresAt = ttl.HasValue ? now.Add(ttl.Value) : null,
            };

            if (scope == MemoryScope.User)
                userEntries.Add(entry);
            else
                projectEntries.Add(entry);
            written++;
        }

        if (skipped > 0)
            _logger.LogWarning("AutoDream: skipped {Count} invalid changes", skipped);

        if (userEntries.Count > 0)
        {
            await _entryStore.UpsertAsync(MemoryScope.User, userEntries, ct).ConfigureAwait(false);
        }
        foreach (var key in userDeleteKeys)
        {
            await _entryStore.RemoveAsync(MemoryScope.User, key, ct).ConfigureAwait(false);
        }

        if (!string.IsNullOrWhiteSpace(projectRoot))
        {
            if (projectEntries.Count > 0)
            {
                await _entryStore.UpsertAsync(MemoryScope.Project, projectEntries, ct).ConfigureAwait(false);
            }
            foreach (var key in projectDeleteKeys)
            {
                await _entryStore.RemoveAsync(MemoryScope.Project, key, ct).ConfigureAwait(false);
            }

            // Prune expired entries after consolidation
            await _entryStore.PruneAsync(MemoryScope.Project, ct).ConfigureAwait(false);
        }

        // Prune user-scope expired entries
        await _entryStore.PruneAsync(MemoryScope.User, ct).ConfigureAwait(false);

        return written;
    }
}
