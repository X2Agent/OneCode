using Microsoft.Agents.AI.Compaction;
using Microsoft.Extensions.AI;

namespace OneCode.Infrastructure.Agent;

/// <summary>
/// MAF 自定义压缩策略：移除重复的 <c>(toolName, args)</c> 调用组，只保留最近一次。
///
/// <para><b>这是 MAF 原生策略没有的能力</b>——<see cref="ToolResultCompactionStrategy"/> 折叠旧 tool call
/// 为摘要，但不按"重复调用"维度去重。本策略在 token 阈值触发前就去掉完全重复的调用，
/// 避免无意义的 token 占用。</para>
///
/// <para><b>去重逻辑</b>：遍历 <see cref="CompactionGroupKind.ToolCall"/> 组，按
/// <c>(FunctionCallContent.Name, JsonSerializer.Serialize(Arguments))</c> 构建 dedup key。
/// 同一 key 出现多次时，排除较早的组，保留最近一次。受 <see cref="MinimumPreservedGroups"/>
/// 保护的尾部组不参与去重。</para>
///
/// <para><b>粒度限制</b>：以 <see cref="CompactionMessageGroup"/> 为原子单位排除，不处理
/// "一个组内部分 tool call 重复"的场景（需要拆分组，复杂度高且 MAF 的 <see cref="ToolResultCompactionStrategy"/>
/// 已通过折叠缓解）。实际场景中，同一 assistant 消息内重复调用同一工具的情况极少。</para>
/// </summary>
public sealed class SnipDuplicateCallsCompactionStrategy : CompactionStrategy
{
    /// <summary>
    /// 默认保留的最近非系统组数——这些组不参与去重，保证当前上下文完整。
    /// 与 <c>CompactConstants.ProtectedTailSize</c> 对齐。
    /// </summary>
    public const int DefaultMinimumPreserved = 10;

    /// <param name="trigger">
    /// 触发条件，默认 <see cref="CompactionTriggers.Always"/>——每次 compaction 都检查重复。
    /// 去重是纯内存操作（无 LLM），始终执行开销可接受。
    /// </param>
    /// <param name="minimumPreservedGroups">
    /// 保留的最近非系统组数（硬下限），这些组不参与去重。
    /// </param>
    public SnipDuplicateCallsCompactionStrategy(
        CompactionTrigger? trigger = null,
        int minimumPreservedGroups = DefaultMinimumPreserved)
        : base(trigger ?? CompactionTriggers.Always)
    {
        this.MinimumPreservedGroups = EnsureNonNegative(minimumPreservedGroups);
    }

    /// <summary>
    /// Gets the minimum number of most-recent non-system groups that are always preserved.
    /// </summary>
    public int MinimumPreservedGroups { get; }

    /// <inheritdoc/>
    protected override ValueTask<bool> CompactCoreAsync(
        CompactionMessageIndex index, ILogger logger, CancellationToken cancellationToken)
    {
        // 收集所有未排除的非系统组索引，计算受保护起始位置
        List<int> nonSystemIncludedIndices = [];
        for (int i = 0; i < index.Groups.Count; i++)
        {
            CompactionMessageGroup group = index.Groups[i];
            if (!group.IsExcluded && group.Kind != CompactionGroupKind.System)
            {
                nonSystemIncludedIndices.Add(i);
            }
        }

        int protectedStart = EnsureNonNegative(
            nonSystemIncludedIndices.Count - this.MinimumPreservedGroups);
        HashSet<int> protectedGroupIndices = [];
        for (int i = protectedStart; i < nonSystemIncludedIndices.Count; i++)
        {
            protectedGroupIndices.Add(nonSystemIncludedIndices[i]);
        }

        // 遍历未受保护的 ToolCall 组，按 (name, args) 构建 dedup key。
        // 同一 key 保留最后一次出现的组索引，排除之前的。
        var dedupKeyToLastIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        var duplicateIndices = new List<int>();

        for (int i = 0; i < index.Groups.Count; i++)
        {
            if (protectedGroupIndices.Contains(i))
                continue;

            CompactionMessageGroup group = index.Groups[i];
            if (group.IsExcluded || group.Kind != CompactionGroupKind.ToolCall)
                continue;

            // 一个 ToolCall 组可能包含多个 FunctionCall（并行工具调用）
            // 只有当组内所有调用都重复时才排除整个组
            var keys = new List<string>();
            bool hasAnyCall = false;

            foreach (ChatMessage message in group.Messages)
            {
                foreach (AIContent content in message.Contents)
                {
                    if (content is FunctionCallContent fcc)
                    {
                        hasAnyCall = true;
                        keys.Add(BuildDedupKey(fcc));
                    }
                }
            }

            if (!hasAnyCall)
                continue;

            // 用所有 key 的组合作为组的 dedup key
            var groupKey = string.Join("||", keys);

            if (dedupKeyToLastIndex.TryGetValue(groupKey, out int prevIndex))
            {
                // 之前的组是重复的，标记排除
                // 但先把之前记录的索引加入 duplicateIndices（它更早）
                // 当前索引成为新的"最后一次"
                duplicateIndices.Add(prevIndex);
                dedupKeyToLastIndex[groupKey] = i;
            }
            else
            {
                dedupKeyToLastIndex[groupKey] = i;
            }
        }

        if (duplicateIndices.Count == 0)
            return new ValueTask<bool>(false);

        foreach (int idx in duplicateIndices)
        {
            index.Groups[idx].IsExcluded = true;
            index.Groups[idx].ExcludeReason = $"Snipped by {nameof(SnipDuplicateCallsCompactionStrategy)} (duplicate tool call)";
        }

        logger.LogDebug(
            "SnipDuplicateCalls: removed {Count} duplicate tool call groups",
            duplicateIndices.Count);

        return new ValueTask<bool>(true);
    }

    private static string BuildDedupKey(FunctionCallContent fcc)
    {
        var args = fcc.Arguments is not null
            ? JsonSerializer.Serialize(fcc.Arguments)
            : "{}";
        return $"{fcc.Name}|{args}";
    }
}
