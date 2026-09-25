using Microsoft.Agents.AI;
using OneCode.App.Services.Compact;
using OneCode.App.Session;

namespace OneCode.App.Services.Agent;

/// <summary>
/// MAF AgentSession 的序列化快照存取：恢复与写回 <see cref="Conversation.Metadata"/>，
/// 并创建桥接事件溯源转录的 <see cref="ChatHistoryProvider"/>。
///
/// provider 私有状态（<see cref="ProviderSessionState{T}"/>）存于 session StateBag，
/// 随快照序列化进 conversation header，跨进程重启可恢复。
/// </summary>
public sealed class AgentSessionPersistence
{
    private const string MafSessionMetadataKey = "mafSession";

    private readonly ISessionConversationAccess _sessionManager;
    private readonly ISessionChatHistoryReader _chatHistoryReader;
    private readonly ILogger<AgentSessionPersistence> _logger;

    public AgentSessionPersistence(
        ISessionConversationAccess sessionManager,
        ISessionChatHistoryReader chatHistoryReader,
        ILogger<AgentSessionPersistence> logger)
    {
        _sessionManager = sessionManager;
        _chatHistoryReader = chatHistoryReader;
        _logger = logger;
    }

    /// <summary>创建桥接会话转录的 <see cref="ChatHistoryProvider"/>：历史读经框架契约，写仍归事件溯源转录。</summary>
    public ChatHistoryProvider CreateChatHistoryProvider(SessionId conversationId)
        => new TranscriptChatHistoryProvider(_chatHistoryReader, conversationId);

    /// <summary>
    /// Restores an <see cref="AgentSession"/> from <see cref="Conversation.Metadata"/> when a prior run
    /// serialized one, otherwise creates a fresh session.
    /// </summary>
    /// <remarks>
    /// 恢复前比对 <see cref="MafSessionInvalidator.HistoryEpochKey"/> 与
    /// <see cref="MafSessionInvalidator.MafSessionEpochKey"/>。不一致说明 mafSession 持久化后消息历史
    /// 发生了结构性变更（compact/clear/snip），drop session 并创建新的，避免 MAF 引用已删消息。
    /// </remarks>
    public async Task<AgentSession> CreateOrRestoreSessionAsync(
        AIAgent agent,
        SessionId? conversationId,
        CancellationToken ct)
    {
        var conversation = conversationId is { } id
            ? _sessionManager?.GetConversation(id)
            : null;
        if (conversation is not null &&
            conversation.Metadata.TryGetValue(MafSessionMetadataKey, out var raw))
        {
            // epoch 比对 — mafSession 持久化时的 epoch 必须与当前 historyEpoch 一致
            var currentEpoch = MafSessionInvalidator.GetHistoryEpoch(conversation);
            var persistedEpoch = GetMafSessionEpoch(conversation);
            if (currentEpoch != persistedEpoch)
            {
                _logger.LogWarning(
                    "mafSession epoch mismatch (current={Current}, persisted={Persisted}), " +
                    "dropping stale session. Last invalidation source: {Source}",
                    currentEpoch, persistedEpoch,
                    conversation.Metadata.TryGetValue("lastMafSessionInvalidationSource", out var src)
                        ? src?.ToString() ?? "(unknown)"
                        : "(none)");
                conversation.Metadata.Remove(MafSessionMetadataKey);
                conversation.Metadata.Remove(MafSessionInvalidator.MafSessionEpochKey);
                return await agent.CreateSessionAsync(ct).ConfigureAwait(false);
            }

            try
            {
                JsonElement json;
                if (raw is JsonElement el)
                {
                    json = el;
                }
                else
                {
                    using var doc = JsonDocument.Parse(raw.ToString() ?? "{}");
                    json = doc.RootElement.Clone();
                }
                if (json.ValueKind == JsonValueKind.Object || json.ValueKind == JsonValueKind.Array)
                {
                    var restored = await agent.DeserializeSessionAsync(json, null, ct)
                        .ConfigureAwait(false);
                    _logger.LogDebug("AgentSessionPersistence session restored from conversation metadata");
                    return restored;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to deserialize MAF session, creating fresh session");
            }
        }

        return await agent.CreateSessionAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Serializes the <see cref="AgentSession"/> back into <see cref="Conversation.Metadata"/> so the
    /// next run can restore it. Persistence to disk is handled upstream by <c>SessionManager.SaveAsync</c>.
    /// </summary>
    public async Task PersistSessionAsync(
        AIAgent agent,
        AgentSession session,
        SessionId? conversationId,
        CancellationToken ct)
    {
        var conversation = conversationId is { } id
            ? _sessionManager?.GetConversation(id)
            : null;
        if (conversation is null)
            return;

        try
        {
            // 序列化是异步的，期间可能有 compact/clear 结构性改动消息历史并递增 historyEpoch。
            // 因此 epoch 必须与快照同时捕获、写回前再校验一次：把「当前」epoch 盖在「更早」的
            // 快照上会让恢复路径误判快照仍然有效，把已压缩掉的历史重新喂给模型。
            var epochAtSnapshot = MafSessionInvalidator.GetHistoryEpoch(conversation);

            var json = await agent.SerializeSessionAsync(session, null, ct).ConfigureAwait(false);

            var currentEpoch = MafSessionInvalidator.GetHistoryEpoch(conversation);
            if (currentEpoch != epochAtSnapshot)
            {
                _logger.LogWarning(
                    "Discarding MAF session snapshot: history epoch changed while serializing " +
                    "(snapshot={Snapshot}, current={Current}). The snapshot references the pre-change history.",
                    epochAtSnapshot, currentEpoch);
                conversation.Metadata.Remove(MafSessionMetadataKey);
                conversation.Metadata.Remove(MafSessionInvalidator.MafSessionEpochKey);
                return;
            }

            conversation.Metadata[MafSessionMetadataKey] = json;
            conversation.Metadata[MafSessionInvalidator.MafSessionEpochKey] = epochAtSnapshot;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to serialize MAF session");
        }
    }

    private static int GetMafSessionEpoch(Conversation conversation)
    {
        if (conversation.Metadata.TryGetValue(MafSessionInvalidator.MafSessionEpochKey, out var raw))
        {
            if (raw is int epoch) return epoch;
            if (raw is JsonElement je && je.ValueKind == JsonValueKind.Number && je.TryGetInt32(out var n))
                return n;
        }
        return 0;
    }
}
