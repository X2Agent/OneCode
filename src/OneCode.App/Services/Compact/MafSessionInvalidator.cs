using Microsoft.Agents.AI;

namespace OneCode.App.Services.Compact;

/// <summary>
/// 使 <c>mafSession</c> 中与消息历史相关的状态失效，避免会话转录与框架会话分叉。
/// 任何结构性删除或清空消息后必须调用。
/// </summary>
/// <remarks>
/// 只丢弃按位置绑定消息的键；待办清单、工作记忆目录等与历史无关的 provider 状态必须跨 compact 存活。
/// 会话根上的本地历史哨兵也一并剔除，由 per-service-call 装饰器在下次调用重新打戳。
/// </remarks>
public static class MafSessionInvalidator
{
    private const string MafSessionMetadataKey = "mafSession";

    /// <summary>
    /// <c>mafSession</c> 中必须随消息历史一起丢弃的 StateBag 键。
    /// </summary>
    /// <remarks>
    /// 仅包含按位置绑定消息的状态：
    /// <list type="bullet">
    ///   <item><description>
    ///     <c>PipelineCompactionStrategy</c> — Harness 挂载压缩 provider 时的状态键。
    ///     Harness 未显式指定 stateKey，因此键名是策略类型名；产品的管道策略类型是
    ///     <c>PipelineCompactionStrategy</c>。保存的是消息分组索引，按位置引用具体消息。
    ///   </description></item>
    ///   <item><description><c>InMemoryChatHistoryProvider</c> — MAF 默认内存历史的状态键。</description></item>
    ///   <item><description>
    ///     <c>compaction</c> — 遗留压缩状态键，当前无写入方，仅用于失效时清除。
    ///     其值是按消息位置绑定的分组索引，跨装配版本没有可信的等价映射，
    ///     因此一次性丢弃、按新历史重建，不做格式转换。
    ///   </description></item>
    /// </list>
    /// 不在列表中的键（会话待办、工作记忆目录、工具审批的 standing rules）与消息位置无关，
    /// 必须原样保留。
    /// </remarks>
    private static readonly string[] HistoryBoundStateKeys =
    [
        "PipelineCompactionStrategy",
        nameof(InMemoryChatHistoryProvider),
        Agent.TranscriptChatHistoryProvider.StateKey,
        LegacyCompactionStateKey,
    ];

    /// <summary>
    /// 遗留 <c>CompactionProvider</c> 状态键。仅用于在失效时清除，当前无写入方。
    /// </summary>
    private const string LegacyCompactionStateKey = "compaction";

    /// <summary>
    /// 会话根属性名，<c>ChatClientAgentSession.ConversationId</c> 的序列化键。
    /// </summary>
    private const string ConversationIdPropertyName = "conversationId";

    /// <summary>
    /// 框架「本地历史」哨兵值。镜像 MAF 内部的
    /// <c>PerServiceCallChatHistoryPersistingChatClient.LocalHistoryConversationId</c>（internal，产品侧取不到）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>HarnessAgent</c> 强制 per-service-call 历史持久化，该装饰器在第一次服务调用后把哨兵写入
    /// <c>ChatClientAgentSession.ConversationId</c>（序列化在会话**根**上，不在 <c>stateBag</c> 里）。
    /// </para>
    /// <para>
    /// <b>为什么失效时一并丢弃</b>：哨兵由旧历史的服务调用写入，属于随历史一起失效的运行时簿记状态；
    /// 结构性变更后丢弃，per-service-call 装饰器会在恢复后的第一次服务调用重新打戳，恢复会话与新建会话
    /// 行为对齐。<c>ConversationId</c> 的 setter 是 internal 且拒绝空值，产品侧只能从持久化快照里剔除。
    /// </para>
    /// <para>
    /// 行为由 <c>HarnessCompactionActivationTests</c> 守卫（含「去掉装饰器后每次服务调用都压缩」的反证）。
    /// </para>
    /// </remarks>
    private const string LocalHistorySentinel = "_agent_local_chat_history";

    /// <summary>
    /// Invalidates the MAF runtime without changing transcript history. Use this when
    /// starting a workflow run that intentionally creates a fresh agent context boundary.
    /// </summary>
    public static void InvalidateRuntime(Conversation conversation, string source)
    {
        conversation.Metadata.Remove(MafSessionMetadataKey);
        conversation.Metadata.Remove(MafSessionEpochKey);
        conversation.Metadata["lastMafSessionInvalidatedAt"] = DateTimeOffset.UtcNow.ToString("O");
        conversation.Metadata["lastMafSessionInvalidationSource"] = source;
    }

    /// <summary>historyEpoch 计数器 key，每次结构性变更递增。</summary>
    public const string HistoryEpochKey = "historyEpoch";

    /// <summary>mafSession 持久化时记录的 epoch 快照 key，用于恢复时比对。</summary>
    public const string MafSessionEpochKey = "mafSessionEpoch";

    /// <summary>
    /// 失效 mafSession 中与消息历史绑定的状态，并记录日志源。
    /// </summary>
    /// <remarks>
    /// 当无法解析已持久化的 mafSession（缺失或格式不符）时，退化为整体删除——
    /// 此时没有可信的非历史状态可保留，保留一个不可解析的快照只会让它继续失效。
    /// </remarks>
    public static void Invalidate(Conversation conversation, string source)
    {
        var pruned = TryPruneHistoryBoundState(conversation);

        conversation.Metadata["lastMafSessionInvalidatedAt"] = DateTimeOffset.UtcNow.ToString("O");
        conversation.Metadata["lastMafSessionInvalidationSource"] = source;

        // 递增 historyEpoch，标记消息历史发生结构性变更。
        var epoch = GetHistoryEpoch(conversation) + 1;
        conversation.Metadata[HistoryEpochKey] = epoch;

        if (pruned)
        {
            // 定向失效：快照里剩下的状态仍然有效，把 epoch 快照同步到新值，
            // 使 CreateOrRestoreSessionAsync 不会因 epoch 不一致而把保留的状态一起丢弃。
            // 被丢弃的只有与消息位置绑定的键，provider 会在下次调用时按新历史重建。
            conversation.Metadata[MafSessionEpochKey] = epoch;
            return;
        }

        // 快照缺失或不可解析：没有可信的非历史状态可保留，退化为整体删除。
        // 保留 epoch key 的缺失状态，让恢复路径按「无会话」处理。
        conversation.Metadata.Remove(MafSessionMetadataKey);
        conversation.Metadata.Remove(MafSessionEpochKey);
    }

    /// <summary>
    /// Removes only the history-bound keys from the persisted session snapshot.
    /// </summary>
    /// <returns><see langword="true"/> when the snapshot was readable and rewritten; otherwise false.</returns>
    private static bool TryPruneHistoryBoundState(Conversation conversation)
    {
        if (!conversation.Metadata.TryGetValue(MafSessionMetadataKey, out var raw))
            return false;

        JsonElement sessionElement;
        if (raw is JsonElement element)
        {
            sessionElement = element;
        }
        else if (raw is string text && !string.IsNullOrWhiteSpace(text))
        {
            try
            {
                using var doc = JsonDocument.Parse(text);
                sessionElement = doc.RootElement.Clone();
            }
            catch (JsonException)
            {
                return false;
            }
        }
        else
        {
            return false;
        }

        if (sessionElement.ValueKind != JsonValueKind.Object
            || !sessionElement.TryGetProperty("stateBag", out var stateBag)
            || stateBag.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var retained = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in stateBag.EnumerateObject())
        {
            if (!HistoryBoundStateKeys.Contains(property.Name, StringComparer.Ordinal))
                retained[property.Name] = property.Value.Clone();
        }

        var prunedSession = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in sessionElement.EnumerateObject())
        {
            // 哨兵是旧历史的运行时簿记戳，随结构性历史变更一并剔除（见 LocalHistorySentinel 的说明）。
            // 只剔除哨兵本身：真·远端 ConversationId 代表服务端托管的历史，丢弃它会切断续跑。
            if (property.Name == ConversationIdPropertyName
                && property.Value.ValueKind == JsonValueKind.String
                && string.Equals(property.Value.GetString(), LocalHistorySentinel, StringComparison.Ordinal))
            {
                continue;
            }

            prunedSession[property.Name] = property.Name == "stateBag"
                ? JsonSerializer.SerializeToElement(retained)
                : property.Value.Clone();
        }

        conversation.Metadata[MafSessionMetadataKey] = JsonSerializer.SerializeToElement(prunedSession);
        return true;
    }

    /// <summary>读取当前 historyEpoch（未初始化时返回 0）。</summary>
    public static int GetHistoryEpoch(Conversation conversation)
    {
        if (conversation.Metadata.TryGetValue(HistoryEpochKey, out var raw) && raw is int epoch)
            return epoch;
        if (raw is JsonElement je && je.ValueKind == JsonValueKind.Number && je.TryGetInt32(out var n))
            return n;
        return 0;
    }
}
