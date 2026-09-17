using OneCode.Core.Session;

namespace OneCode.App.Session;

/// <summary>进程内会话事件存储，用于事件 projection、回放和单元测试。</summary>
public sealed class InMemorySessionEventStore : ISessionEventStore
{
    private readonly Lock _gate = new();
    private readonly Dictionary<SessionId, List<SessionEvent>> _events = [];

    /// <inheritdoc />
    public Task<IReadOnlyList<long>> AppendAsync(
        IReadOnlyList<SessionEvent> events,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(events);
        if (events.Count == 0)
            return Task.FromResult<IReadOnlyList<long>>([]);

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
            var sequences = new long[events.Count];
            for (var index = 0; index < events.Count; index++)
            {
                var sequence = firstSequence + index;
                sessionEvents.Add(events[index].WithSequence(sequence));
                sequences[index] = sequence;
            }

            return Task.FromResult<IReadOnlyList<long>>(sequences);
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
    public Task<IReadOnlyList<SessionId>> ListSessionsAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
            return Task.FromResult<IReadOnlyList<SessionId>>(_events.Keys.ToArray());
    }

    /// <inheritdoc />
    public Task DeleteAsync(SessionId sessionId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
            _events.Remove(sessionId);
        return Task.CompletedTask;
    }
}