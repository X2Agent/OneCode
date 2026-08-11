namespace OneCode.Core.Domain;

/// <summary>
/// Standalone implementation of <see cref="ISessionIdProvider"/> that holds the current
/// session ID in a mutable field. Breaks the circular dependency between SessionManager
/// and TokenUsageTracker: SessionManager updates the holder when the foreground session
/// changes, TokenUsageTracker reads from the holder without depending on SessionManager.
/// </summary>
public sealed class SessionIdHolder : ISessionIdProvider
{
    private readonly Lock _lock = new();
    private SessionId? _current;

    public SessionId? CurrentSessionId
    {
        get { lock (_lock) { return _current; } }
    }

    public void SetCurrent(SessionId? sessionId)
    {
        lock (_lock) { _current = sessionId; }
    }
}
