using OneCode.Core.Session;

namespace OneCode.App.Session;

/// <summary>以 EventLog 为事实源、以 projection 重建 Conversation 的会话存储。</summary>
public sealed class EventSourcedSessionStore(
    ISessionEventStore eventStore,
    ISessionSummaryIndex summaryIndex,
    ILogger<EventSourcedSessionStore> logger) : ISessionStore
{
    private readonly ConcurrentDictionary<SessionId, SemaphoreSlim> _sessionLocks = new();

    // 侧栏摘要缓存：日志戳未变即复用，避免每次列表都全量解析所有会话的事件日志。
    private readonly ConcurrentDictionary<SessionId, CachedSummary> _summaries = new();

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

            var stamp = await eventStore.AppendAsync(events, ct).ConfigureAwait(false);
            if (stamp is not { } written)
                return;

            // 写入返回的日志戳直接用于回填内存缓存与落盘索引：
            // 下一次列表请求既不用重新解析刚写过的大日志，也不用再枚举目录取戳。
            var summary = CreateSummary(conversation);
            _summaries[conversation.Id] = new CachedSummary(written, summary);
            await summaryIndex.WriteAsync(written, summary, ct).ConfigureAwait(false);
        }
        finally
        {
            sessionLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ConversationSummary>> ListAsync(CancellationToken ct = default)
    {
        var watch = Stopwatch.StartNew();
        var stamps = await eventStore.ListLogsAsync(ct).ConfigureAwait(false);
        var summaries = new List<ConversationSummary>(stamps.Count);
        var liveSessions = new HashSet<SessionId>(stamps.Count);
        var cacheHits = 0;
        var parsedLogs = 0;
        foreach (var stamp in stamps)
        {
            liveSessions.Add(stamp.SessionId);
            if (_summaries.TryGetValue(stamp.SessionId, out var cached) && cached.Stamp == stamp)
            {
                summaries.Add(cached.Summary);
                cacheHits++;
                continue;
            }

            // 内存未命中再查落盘索引：命中只需读一个几十字节的小文件。
            var summary = await summaryIndex.ReadAsync(stamp, ct).ConfigureAwait(false);
            if (summary is null)
            {
                var events = await eventStore.ReadAsync(stamp.SessionId, ct).ConfigureAwait(false);
                var conversation = ToConversation(events);
                if (conversation is null)
                    continue;

                parsedLogs++;
                summary = CreateSummary(conversation);
                await summaryIndex.WriteAsync(stamp, summary, ct).ConfigureAwait(false);
            }
            else
            {
                cacheHits++;
            }

            _summaries[stamp.SessionId] = new CachedSummary(stamp, summary);
            summaries.Add(summary);
        }

        // 已删除的会话不留在缓存里，避免陈旧命中与长期占用。
        foreach (var sessionId in _summaries.Keys)
        {
            if (!liveSessions.Contains(sessionId))
                _summaries.TryRemove(sessionId, out _);
        }

        logger.LogInformation(
            "会话列表加载 {Count} 个：索引命中 {Hits}，解析日志 {Parsed}，耗时 {Elapsed}ms",
            summaries.Count, cacheHits, parsedLogs, watch.ElapsedMilliseconds);

        return summaries;
    }

    /// <inheritdoc />
    public async Task DeleteAsync(SessionId sessionId, CancellationToken ct = default)
    {
        _summaries.TryRemove(sessionId, out _);
        // 先删索引再删日志：中途失败最多留下孤儿索引（不会被读取），不会留下指向已删日志的摘要。
        await summaryIndex.RemoveAsync(sessionId, ct).ConfigureAwait(false);
        await eventStore.DeleteAsync(sessionId, ct).ConfigureAwait(false);
    }

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

    private static ConversationSummary CreateSummary(Conversation conversation) => new(
        conversation.Id,
        conversation.DisplayTitle(),
        conversation.Model,
        conversation.Messages.Count,
        conversation.CreatedAt,
        conversation.LastActivityAt,
        conversation.TotalUsage,
        Mode: GetMode(conversation));

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

    private sealed record CachedSummary(SessionEventLogStamp Stamp, ConversationSummary Summary);

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
