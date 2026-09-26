using OneCode.Core.Domain;
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
                && !MessagesMatch(existingMessages, conversation.Messages);

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
                conversation.Name,
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
    public async Task<SessionResume?> LoadForResumeAsync(
        SessionId sessionId,
        CancellationToken ct = default)
    {
        var conversation = await LoadAsync(sessionId, ct).ConfigureAwait(false);
        if (conversation is null || conversation.Messages.Count == 0)
            return null;

        var resolvedToolUseIds = conversation.Messages
            .OfType<ToolResultMessage>()
            .Select(message => message.ToolUseId)
            .ToHashSet(StringComparer.Ordinal);
        var messages = conversation.Messages
            .Where(message => message is not AssistantMessage assistant
                || assistant.Content.OfType<ToolUseBlock>().All(tool => resolvedToolUseIds.Contains(tool.Id)))
            .ToList();
        var lastMessage = messages.FindLast(message =>
            message.Role is not MessageRole.System and not MessageRole.Tool);
        var title = messages.OfType<UserMessage>().FirstOrDefault()?.Content.TrimStart();

        return new SessionResume(
            sessionId,
            messages,
            lastMessage?.Role switch
            {
                MessageRole.User => InterruptionState.InterruptedPrompt,
                MessageRole.Attachment => InterruptionState.InterruptedTurn,
                _ => InterruptionState.None,
            },
            conversation.LastActivityAt,
            string.IsNullOrEmpty(title) ? "(untitled)" : title,
            messages.Count);
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
        AttachmentMessageEvent item => item.Message.Id,
        TombstoneMessageEvent item => item.Message.Id,
        _ => null,
    };

    private static bool MessagesMatch(
        IReadOnlyList<Message> existing,
        IReadOnlyList<Message> current)
    {
        if (existing.Count != current.Count)
            return false;

        for (var index = 0; index < current.Count; index++)
        {
            if (!existing[index].Equals(current[index]))
                return false;
        }

        return true;
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