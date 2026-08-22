namespace OneCode.App.Session;

public interface ISessionStore
{
    Task<Conversation?> LoadAsync(SessionId conversationId, CancellationToken ct = default);

    Task SaveAsync(Conversation conversation, CancellationToken ct = default);

    /// <summary>
    /// List persisted sessions by reading only each file's header line —
    /// messages are not loaded, so <see cref="ConversationSummary.MessageCount"/>
    /// comes from the header's persisted count.
    /// </summary>
    Task<IReadOnlyList<ConversationSummary>> ListAsync(CancellationToken ct = default);

    Task<SessionResume?> LoadForResumeAsync(SessionId sessionId, CancellationToken ct = default);

    void Delete(SessionId sessionId);
}

/// <summary>
/// Header-level summary of a persisted session, for listing and resume choosers.
/// </summary>
public sealed record ConversationSummary(
    SessionId Id,
    string Name,
    string Model,
    int MessageCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastActivityAt,
    string? Mode = null,
    TokenUsage TotalUsage = default);
