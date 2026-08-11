using OneCode.Core.Domain;

namespace OneCode.Tests.TestSupport;

/// <summary>
/// Test-only ISessionIdProvider that always returns null (no active session).
/// </summary>
public sealed class NullSessionIdProvider : ISessionIdProvider
{
    public static NullSessionIdProvider Instance { get; } = new();

    public SessionId? CurrentSessionId => null;
}
