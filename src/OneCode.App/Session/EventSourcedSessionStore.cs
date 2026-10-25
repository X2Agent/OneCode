using OneCode.Core.Session;

namespace OneCode.App.Session;

/// <summary>以 EventLog 为事实源、以 projection 重建 Conversation 的会话存储。</summary>
public sealed class EventSourcedSessionStore(ISessionEventStore eventStore) : ISessionStore
{
    private readonly ConcurrentDictionary<SessionId, SemaphoreSlim> _sessionLocks = new();

    /// <inheritdoc />
    public async Task<Conversation?> LoadAsync(
        SessionId conversationId,
        CancellationToken ct = default)
    {
        var events = await eventStore.ReadAsync(conversationId, ct).ConfigureAwait(false);
        return events.Count == 0 ? null : ToConversation(events);
    }

    /// <inheritdoc />
    public async Task SaveAsync(Conversation conversation, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        var sessionLock = GetSessionLock(conversation.Id);
        await sessionLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var existing = await eventStore.ReadAsync(conversation.Id, ct).ConfigureAwait(false);
            var existingMessages = SessionEventProjection.DeriveMessages(existing);
            var existingSnapshot = SessionEventProjection.FindLatestSessionSnapshot(existing);
            var existingMessageIds = existingMessages
                .Select(message => message.Id)
                .ToHashSet(StringComparer.Ordinal);
            var replacementRequired = existing.Count > 0
                && !IsPrefixMatch(existingMessages, conversation.Messages);

            List<SessionEvent> events = [];

            if (existingSnapshot is null || !SnapshotsMatch(existingSnapshot, conversation))
            {
                events.Add(new SessionSnapshotEvent(
                    conversation.Id,
                    0,
                    conversation.LastActivityAt,
                    conversation.Name,
                    conversation.WorkingDirectory,
                    conversation.Model,
                    conversation.Status,
                    conversation.TotalUsage,
                    conversation.CreatedAt,
                    conversation.LastActivityAt,
                    conversation.Branch,
                    conversation.Metadata));
            }

            if (replacementRequired)
            {
                events.Add(new MessagesReplacedEvent(
                    conversation.Id,
                    0,
                    conversation.LastActivityAt,
                    conversation.Messages.ToArray(),
                    GetReplacementReason(conversation)));
                existingMessageIds = conversation.Messages
                    .Select(message => message.Id)
                    .ToHashSet(StringComparer.Ordinal);
            }

            events.AddRange(ConversationEventMapper.ToEvents(conversation)
                .Where(sessionEvent => sessionEvent is not SessionStartedEvent
                    && sessionEvent is not SessionSnapshotEvent
                    && TryGetMessageId(sessionEvent) is { } messageId
                    && existingMessageIds.Add(messageId)));

            await eventStore.AppendAsync(events, ct).ConfigureAwait(false);
        }
        finally
        {
            sessionLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ConversationSummary>> ListAsync(CancellationToken ct = default)
    {
        var sessionIds = await eventStore.ListSessionsAsync(ct).ConfigureAwait(false);
        var summaries = new List<ConversationSummary>(sessionIds.Count);
        foreach (var sessionId in sessionIds)
        {
            var events = await eventStore.ReadAsync(sessionId, ct).ConfigureAwait(false);
            var conversation = ToConversation(events);
            if (conversation is null)
                continue;

            summaries.Add(new ConversationSummary(
                conversation.Id,
                conversation.DisplayTitle(),
                conversation.Model,
                conversation.Messages.Count,
                conversation.CreatedAt,
                conversation.LastActivityAt,
                Mode: GetMode(conversation),
                TotalUsage: conversation.TotalUsage));
        }

        return summaries;
    }

    /// <inheritdoc />
    public Task DeleteAsync(SessionId sessionId, CancellationToken ct = default) =>
        eventStore.DeleteAsync(sessionId, ct);

    private static Conversation? ToConversation(IReadOnlyList<SessionEvent> events)
    {
        var snapshot = SessionEventProjection.FindLatestSessionSnapshot(events);
        if (snapshot is null)
            return null;

        var conversation = new Conversation
        {
            Id = snapshot.SessionId,
            Name = snapshot.Name,
            WorkingDirectory = snapshot.WorkingDirectory,
            Model = snapshot.Model,
            Status = snapshot.Status,
            TotalUsage = snapshot.TotalUsage,
            CreatedAt = snapshot.CreatedAt,
            LastActivityAt = snapshot.LastActivityAt,
            Branch = snapshot.Branch,
        };
        if (snapshot.Metadata is not null)
        {
            foreach (var (key, value) in snapshot.Metadata)
                conversation.Metadata[key] = value;
        }

        conversation.Messages.AddRange(SessionEventProjection.DeriveMessages(events));
        return conversation;
    }

    private static string? GetMode(Conversation conversation) =>
        conversation.Metadata.TryGetValue("mode", out var mode) ? mode.ToString() : null;

    private static string GetReplacementReason(Conversation conversation) =>
        conversation.Metadata.TryGetValue("lastMafSessionInvalidationSource", out var source)
            && !string.IsNullOrWhiteSpace(source?.ToString())
            ? source.ToString()!
            : "conversation-state-replaced";

    private SemaphoreSlim GetSessionLock(SessionId sessionId) =>
        _sessionLocks.GetOrAdd(sessionId, static _ => new SemaphoreSlim(1, 1));

    private static string? TryGetMessageId(SessionEvent sessionEvent) => sessionEvent switch
    {
        UserMessageEvent item => item.Message.Id,
        SystemMessageEvent item => item.Message.Id,
        AssistantMessageEvent item => item.Message.Id,
        ToolResultEvent item => item.Message.Id,
        _ => null,
    };

    /// <summary>
    /// 判断已持久化的消息序列是否为当前对话消息列表的前缀。
    /// 正常追加新消息时返回 <c>true</c>（仅追加增量事件）；
    /// 仅当消息被截断、顺序改变或既有消息内容被改写（如 /compact、/rewind）时返回 <c>false</c>（发射 <see cref="MessagesReplacedEvent"/>）。
    /// </summary>
    private static bool IsPrefixMatch(
        IReadOnlyList<Message> existing,
        IReadOnlyList<Message> current)
    {
        if (existing.Count > current.Count)
            return false;

        for (var index = 0; index < existing.Count; index++)
        {
            if (!SingleMessageMatches(existing[index], current[index]))
                return false;
        }

        return true;
    }

    private static bool SingleMessageMatches(Message left, Message right)
    {
        if (!string.Equals(left.Id, right.Id, StringComparison.Ordinal) || left.Role != right.Role)
            return false;

        return (left, right) switch
        {
            (UserMessage lUser, UserMessage rUser) =>
                string.Equals(lUser.Content, rUser.Content, StringComparison.Ordinal)
                && lUser.IsMeta == rUser.IsMeta,
            (SystemMessage lSys, SystemMessage rSys) =>
                string.Equals(lSys.Content, rSys.Content, StringComparison.Ordinal),
            (ToolResultMessage lTool, ToolResultMessage rTool) =>
                string.Equals(lTool.ToolUseId, rTool.ToolUseId, StringComparison.Ordinal)
                && string.Equals(lTool.ToolName, rTool.ToolName, StringComparison.Ordinal)
                && string.Equals(lTool.Content, rTool.Content, StringComparison.Ordinal)
                && lTool.IsError == rTool.IsError,
            (AssistantMessage lAsst, AssistantMessage rAsst) =>
                lAsst.Content.SequenceEqual(rAsst.Content),
            _ => left.Equals(right),
        };
    }

    private static bool SnapshotsMatch(SessionStartedEvent snapshot, Conversation conversation)
    {
        if (!string.Equals(snapshot.Name, conversation.Name, StringComparison.Ordinal)
            || !string.Equals(snapshot.WorkingDirectory, conversation.WorkingDirectory, StringComparison.Ordinal)
            || !string.Equals(snapshot.Model, conversation.Model, StringComparison.Ordinal)
            || snapshot.Status != conversation.Status
            || !snapshot.TotalUsage.Equals(conversation.TotalUsage)
            || snapshot.Branch != conversation.Branch)
            return false;

        var metadata = snapshot.Metadata;
        if (metadata is null)
            return conversation.Metadata.Count == 0;
        if (metadata.Count != conversation.Metadata.Count)
            return false;
        foreach (var (key, value) in conversation.Metadata)
        {
            if (!metadata.TryGetValue(key, out var snapshotValue)
                || !Equals(snapshotValue, value))
                return false;
        }

        return true;
    }
}
