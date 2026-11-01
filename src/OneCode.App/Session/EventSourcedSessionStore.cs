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

    /// <summary>
    /// 各类缓存条目上限。缓存按会话键增长，进程存活期间会遍历到历史全部会话；
    /// 超出后按插入顺序丢弃最旧的条目（下次访问时按需重算，正确性不依赖缓存）。
    /// </summary>
    private const int MaxCachedSessions = 256;

    /// <summary>事件日志快照压缩阈值（字节）：超过后在保存收尾时折叠为快照 + 全量消息替换。</summary>
    private const long CompactionThresholdBytes = 2 * 1024 * 1024;

    /// <summary>
    /// 各会话上次压缩后的日志体积。下次压缩门槛抬到「该体积 + 50%」：消息体本身超过
    /// 阈值的会话压缩后仍会超阈值，若无此闸则每轮 SaveAsync 都要整文件重写一次。
    /// </summary>
    private readonly ConcurrentDictionary<SessionId, long> _compactedBytes = new();

    /// <summary>
    /// 进程内事件投影缓存：<c>SaveAsync</c> 每次都要判定「已落盘消息是否为当前消息列表的前缀」，
    /// 重读整份事件日志会让写入成本随日志长度线性增长（长会话每轮保存都要解析数 MB JSONL）。
    /// 缓存以日志戳（长度 + 最后写入时间）为有效性凭据，自身写入后立即回填。
    /// </summary>
    private readonly ConcurrentDictionary<SessionId, CachedProjection> _projections = new();

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
            var projection = await ReadProjectionAsync(conversation.Id, ct).ConfigureAwait(false);
            var existing = projection.Events;
            var existingMessages = projection.Messages;
            var existingSnapshot = projection.Snapshot;
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

            // 自身写入后立即回填投影：下一次保存无需重读刚写过的日志。
            CacheProjection(conversation.Id, written, [.. existing, .. events]);

            // 写入返回的日志戳直接用于回填内存缓存与落盘索引：
            // 下一次列表请求既不用重新解析刚写过的大日志，也不用再枚举目录取戳。
            var summary = CreateSummary(conversation);
            _summaries[conversation.Id] = new CachedSummary(written, summary);
            await summaryIndex.WriteAsync(written, summary, ct).ConfigureAwait(false);
            TrimCaches();

            // 快照压缩：事件日志超阈值时折叠为「快照 + 全量消息替换 + 标记事件」。
            // 只丢弃消息增量事件（体积主体），marker（epoch/turn）语义完整保留；
            // 低频 O(n) 重写，摊还后追加路径仍维持 O(Δ)。
            // 门槛随上次压缩结果抬高 50%：压缩后仍超阈值的会话不会每轮都被重写。
            var compactionFloor = _compactedBytes.TryGetValue(conversation.Id, out var lastCompactedBytes)
                ? Math.Max(CompactionThresholdBytes, lastCompactedBytes + (lastCompactedBytes / 2))
                : CompactionThresholdBytes;
            if (written.Length > compactionFloor)
            {
                var compacted = new List<SessionEvent>
                {
                    new SessionSnapshotEvent(
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
                        conversation.Metadata),
                    new MessagesReplacedEvent(
                        conversation.Id,
                        0,
                        conversation.LastActivityAt,
                        conversation.Messages.ToArray(),
                        "snapshot-compaction"),
                };
                compacted.AddRange(existing.OfType<SessionMarkerEvent>());

                if (await eventStore.RewriteAsync(conversation.Id, compacted, written, ct).ConfigureAwait(false)
                    is { } compactStamp)
                {
                    logger.LogInformation(
                        "Compacted session {SessionId} event log: {OldBytes:N0} -> {NewBytes:N0} bytes ({Events} events -> {CompactEvents})",
                        conversation.Id, written.Length, compactStamp.Length, existing.Count, compacted.Count);
                    _summaries[conversation.Id] = new CachedSummary(compactStamp, summary);
                    _compactedBytes[conversation.Id] = compactStamp.Length;
                    await summaryIndex.WriteAsync(compactStamp, summary, ct).ConfigureAwait(false);

                    // 压缩把日志重排为「快照 + 全量消息替换 + marker」：缓存必须换成重写后的
                    // 序列号版本，否则下一次保存会带着旧序列号与新日志长度做前缀判定。
                    var rewritten = await eventStore.ReadAsync(conversation.Id, ct).ConfigureAwait(false);
                    CacheProjection(conversation.Id, compactStamp, rewritten);
                }
            }
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

        foreach (var sessionId in _compactedBytes.Keys)
        {
            if (!liveSessions.Contains(sessionId))
                _compactedBytes.TryRemove(sessionId, out _);
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
        _compactedBytes.TryRemove(sessionId, out _);
        _projections.TryRemove(sessionId, out _);
        // 会话写入锁从字典移除即可：另一线程可能正持有/等待同把信号量（SaveAsync 并发进行时），
        // Dispose 会让它的 Release/Wait 抛 ObjectDisposedException。移出字典后新调用方自建新锁，
        // 旧实例交由 GC 回收——不做这一步字典才会随历史会话数单调增长。
        _sessionLocks.TryRemove(sessionId, out _);
        // 先删索引再删日志：中途失败最多留下孤儿索引（不会被读取），不会留下指向已删日志的摘要。
        await summaryIndex.RemoveAsync(sessionId, ct).ConfigureAwait(false);
        await eventStore.DeleteAsync(sessionId, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 取会话事件投影，命中进程内缓存时不碰磁盘。缓存以日志戳（长度 + 最后写入时间）为凭据：
    /// 戳变了说明有其它进程或路径写过日志，必须重读。
    /// </summary>
    private async Task<CachedProjection> ReadProjectionAsync(SessionId sessionId, CancellationToken ct)
    {
        var stamp = await eventStore.GetLogStampAsync(sessionId, ct).ConfigureAwait(false);
        if (stamp is not { } currentStamp)
            return new CachedProjection(default, [], [], null);

        if (_projections.TryGetValue(sessionId, out var cached) && cached.Stamp == currentStamp)
            return cached;

        var events = await eventStore.ReadAsync(sessionId, ct).ConfigureAwait(false);
        var projection = new CachedProjection(
            currentStamp,
            events,
            SessionEventProjection.DeriveMessages(events),
            SessionEventProjection.FindLatestSessionSnapshot(events));
        _projections[sessionId] = projection;
        return projection;
    }

    /// <summary>自身写入后回填投影，下一次保存无需重读刚写过的日志。</summary>
    private void CacheProjection(SessionId sessionId, SessionEventLogStamp stamp, IReadOnlyList<SessionEvent> events)
        => _projections[sessionId] = new CachedProjection(
            stamp,
            events,
            SessionEventProjection.DeriveMessages(events),
            SessionEventProjection.FindLatestSessionSnapshot(events));

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

    /// <summary>
    /// 把各类按会话增长的缓存裁到上限。只丢弃最旧的条目——它们下次访问会按需重建，
    /// 因而裁剪不影响正确性。删除路径另行精确移除，不依赖这里。
    /// </summary>
    private void TrimCaches()
    {
        TrimOldest(_summaries, MaxCachedSessions);
        TrimOldest(_compactedBytes, MaxCachedSessions);
        TrimOldest(_projections, MaxCachedSessions);
    }

    private static void TrimOldest<TValue>(
        ConcurrentDictionary<SessionId, TValue> cache,
        int maxEntries)
    {
        var overflow = cache.Count - maxEntries;
        if (overflow <= 0)
            return;

        // ConcurrentDictionary 不保证顺序；按枚举顺序丢弃即可满足「有界」目标。
        foreach (var key in cache.Keys)
        {
            if (overflow-- <= 0)
                return;
            cache.TryRemove(key, out _);
        }
    }

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

    /// <summary>
    /// 事件投影缓存：日志戳 + 事件列表 + 由其派生的消息与最新快照。
    /// 追加路径只需后两者，但压缩与回写会复用事件列表本身，故一并保留。
    /// </summary>
    private sealed record CachedProjection(
        SessionEventLogStamp Stamp,
        IReadOnlyList<SessionEvent> Events,
        IReadOnlyList<Message> Messages,
        SessionStartedEvent? Snapshot);

    private static bool SnapshotsMatch(SessionStartedEvent snapshot, Conversation conversation)
    {
        if (!string.Equals(snapshot.Name, conversation.Name, StringComparison.Ordinal)
            || !string.Equals(snapshot.WorkingDirectory, conversation.WorkingDirectory, StringComparison.Ordinal)
            || !string.Equals(snapshot.Model, conversation.Model, StringComparison.Ordinal)
            || snapshot.Status != conversation.Status
            || !snapshot.TotalUsage.Equals(conversation.TotalUsage))
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
