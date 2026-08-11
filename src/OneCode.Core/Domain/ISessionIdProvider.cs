namespace OneCode.Core.Domain;

/// <summary>
/// Provides the current session ID for consumers that need to look up
/// per-session state (e.g. token usage) without a direct dependency on
/// <c>ISessionManager</c>, avoiding circular constructor dependencies.
/// </summary>
public interface ISessionIdProvider
{
    SessionId? CurrentSessionId { get; }
}
