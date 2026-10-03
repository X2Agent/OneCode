namespace OneCode.App.Session;

public interface ISessionStore
{
    Task<Conversation?> LoadAsync(SessionId conversationId, CancellationToken ct = default);

    Task SaveAsync(Conversation conversation, CancellationToken ct = default);

    Task<IReadOnlyList<ConversationSummary>> ListAsync(CancellationToken ct = default);

    Task<SessionResume?> LoadForResumeAsync(SessionId sessionId, CancellationToken ct = default);

    Task DeleteAsync(SessionId sessionId, CancellationToken ct = default);
}

/// <summary>
/// Header-level summary of a persisted session, for listing and resume choosers.
/// <see cref="Title"/> 为显示标题（用户命名优先，否则首条用户消息截断），可能为 null。
/// </summary>
public sealed record ConversationSummary(
    SessionId Id,
    string? Title,
    string Model,
    int MessageCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastActivityAt,
    string? Mode = null,
    TokenUsage TotalUsage = default);
