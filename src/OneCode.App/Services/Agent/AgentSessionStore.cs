using Microsoft.Agents.AI;
using OneCode.App.Services.Compact;
using OneCode.App.Session;

namespace OneCode.App.Services.Agent;

/// <summary>
/// MAF AgentSession 持久化与恢复。
///
/// MAF "local session state" 持久化模式：
/// <see cref="ProviderSessionState{T}"/> 值（被 memory providers 使用）存放于 session StateBag，
/// 通过序列化携带，只要序列化的 JSON 存储在 conversation header 中即可跨进程重启恢复。
/// </summary>
public sealed class AgentSessionStore
{
    private const string MafSessionMetadataKey = "mafSession";

    private readonly ISessionConversationAccess _sessionManager;
    private readonly ILogger<AgentSessionStore> _logger;

    public AgentSessionStore(
        ISessionConversationAccess sessionManager,
        ILogger<AgentSessionStore> logger)
    {
        _sessionManager = sessionManager;
        _logger = logger;
    }

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
                    _logger.LogDebug("AgentSessionStore session restored from conversation metadata");
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
            var json = await agent.SerializeSessionAsync(session, null, ct).ConfigureAwait(false);
            conversation.Metadata[MafSessionMetadataKey] = json;

            // 持久化 mafSession 时记录当前 historyEpoch 快照，
            // 恢复时比对以检测结构性变更（compact/clear/snip）。
            conversation.Metadata[MafSessionInvalidator.MafSessionEpochKey] =
                MafSessionInvalidator.GetHistoryEpoch(conversation);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to serialize MAF session");
        }
    }

    /// <summary>读取 mafSession 持久化时的 epoch 快照（未记录时返回 0）。</summary>
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
