using OneCode.Core.Session;

namespace OneCode.App.Session;

/// <summary>进程内会话事件存储，用于事件 projection、回放和单元测试。</summary>
public sealed class InMemorySessionEventStore : ISessionEventStore
{
    private readonly Lock _gate = new();
    private readonly Dictionary<SessionId, List<SessionEvent>> _events = [];
    private readonly Dictionary<SessionId, SessionEventLogStamp> _stamps = [];

    /// <inheritdoc />
    public Task<SessionEventLogStamp?> AppendAsync(
        IReadOnlyList<SessionEvent> events,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(events);
        if (events.Count == 0)
            return Task.FromResult<SessionEventLogStamp?>(null);

        var sessionId = events[0].SessionId;
        if (events.Any(sessionEvent => sessionEvent.SessionId != sessionId))
            throw new ArgumentException("All events in a batch must belong to the same session.", nameof(events));

        lock (_gate)
        {
            if (!_events.TryGetValue(sessionId, out var sessionEvents))
            {
                sessionEvents = [];
                _events.Add(sessionId, sessionEvents);
            }

            var firstSequence = sessionEvents.Count + 1L;
            for (var index = 0; index < events.Count; index++)
                sessionEvents.Add(events[index].WithSequence(firstSequence + index));

            // 内存实现没有文件长度：以事件条数充当 Length，追加即让日志戳变化。
            var stamp = new SessionEventLogStamp(sessionId, sessionEvents.Count, DateTimeOffset.UtcNow);
            _stamps[sessionId] = stamp;
            return Task.FromResult<SessionEventLogStamp?>(stamp);
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<SessionEvent>> ReadAsync(
        SessionId sessionId,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        lock (_gate)
        {
            if (!_events.TryGetValue(sessionId, out var sessionEvents))
                return Task.FromResult<IReadOnlyList<SessionEvent>>([]);

            return Task.FromResult<IReadOnlyList<SessionEvent>>(sessionEvents.ToArray());
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<SessionEventLogStamp>> ListLogsAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
            return Task.FromResult<IReadOnlyList<SessionEventLogStamp>>(_stamps.Values.ToArray());
    }

    /// <inheritdoc />
    public Task DeleteAsync(SessionId sessionId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            _events.Remove(sessionId);
            _stamps.Remove(sessionId);
        }
        return Task.CompletedTask;
    }
}
