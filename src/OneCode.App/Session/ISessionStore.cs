namespace OneCode.App.Session;

public interface ISessionStore
{
    Task<Conversation?> LoadAsync(SessionId conversationId, CancellationToken ct = default);

    Task SaveAsync(Conversation conversation, CancellationToken ct = default);

    Task<IReadOnlyList<Conversation>> ListAsync(CancellationToken ct = default);

    Task<IReadOnlyList<SessionSummary>> ListSessionsAsync(
        int? limit = null,
        int? offset = null,
        CancellationToken ct = default);

    Task<SessionResume?> LoadForResumeAsync(SessionId sessionId, CancellationToken ct = default);

    void Delete(SessionId sessionId);
}

/// <summary>
/// Summary of a session for listing/searching.
/// </summary>
public sealed record SessionSummary(
    SessionId Id,
    string Title,
    string? Model = null,
    int MessageCount = 0,
    DateTimeOffset LastActivityAt = default,
    string? FirstMessage = null);
