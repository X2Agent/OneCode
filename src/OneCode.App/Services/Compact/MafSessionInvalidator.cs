namespace OneCode.App.Services.Compact;

/// <summary>
/// 失效 Conversation.Metadata 中的 mafSession，避免双源分叉。
/// 在 App 层任何结构性删除/清空消息后必须调用。
/// </summary>
public static class MafSessionInvalidator
{
    private const string MafSessionMetadataKey = "mafSession";

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

    /// <summary>失效 mafSession 并记录日志源。</summary>
    public static void Invalidate(Conversation conversation, string source)
    {
        conversation.Metadata.Remove(MafSessionMetadataKey);
        conversation.Metadata.Remove(MafSessionEpochKey);
        conversation.Metadata["lastMafSessionInvalidatedAt"] = DateTimeOffset.UtcNow.ToString("O");
        conversation.Metadata["lastMafSessionInvalidationSource"] = source;

        // 递增 historyEpoch，标记消息历史发生结构性变更。
        // CreateOrRestoreSessionAsync 恢复时会比对 mafSessionEpoch 与 historyEpoch，
        // 不一致则 drop session（防御 mafSession key 残留但内容已过时的竞态/遗漏场景）。
        var epoch = GetHistoryEpoch(conversation);
        conversation.Metadata[HistoryEpochKey] = epoch + 1;
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
