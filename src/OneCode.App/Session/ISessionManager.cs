using Microsoft.Extensions.AI;

namespace OneCode.App.Session;

/// <summary>
/// Abstraction for session/conversation management — creation, resume, persistence, and listing.
/// Aggregates the narrow read interfaces for consumers that need the full surface.
/// New consumers should prefer <see cref="ISessionConversationAccess"/> or
/// <see cref="ISessionWorkingDirectory"/> when those suffice.
/// </summary>
public interface ISessionManager : ISessionConversationAccess, ISessionWorkingDirectory, IAsyncDisposable
{
    IReadOnlyList<BackgroundSession> BackgroundSessions { get; }
    int BackgroundSessionCount { get; }

    Task AppendUserMessageAsync(string content, CancellationToken ct = default);
    Task AppendUserMessageAsync(SessionId conversationId, string content, CancellationToken ct = default);
    Task AppendAssistantMessageAsync(string content, TokenUsage? usage = null, CancellationToken ct = default);
    Task AppendAssistantMessageAsync(SessionId conversationId, string content, TokenUsage? usage = null, CancellationToken ct = default);
    Task AppendAssistantMessageAsync(SessionId conversationId, string content, TokenUsage? usage = null, IReadOnlyList<ToolUseBlock>? toolCalls = null, CancellationToken ct = default);
    Task AppendCompletedToolBatchesAsync(SessionId conversationId, IReadOnlyList<CompletedToolBatch> batches, CancellationToken ct = default);
    IReadOnlyList<ChatMessage> GetForegroundChatHistory();
    IReadOnlyList<ChatMessage> GetChatHistory(SessionId conversationId);

    Task SaveAsync(CancellationToken ct = default);
    Task PersistTranscriptAsync(CancellationToken ct = default);

    Task<Conversation> EnsureActiveSessionAsync(ConversationOptions options, CancellationToken ct = default);
    Task<Conversation> CreateAsync(ConversationOptions options, CancellationToken ct = default);
    Task<Conversation?> ResumeAsync(string conversationId, CancellationToken ct = default);
    Task CloseAsync(CancellationToken ct = default);
    Task<Conversation?> SwitchToSessionAsync(string conversationId, CancellationToken ct = default);
    Task<Conversation> BackgroundCurrentAndCreateNewAsync(ConversationOptions options, CancellationToken ct = default);
    Task<bool> CloseBackgroundSessionAsync(string conversationId, CancellationToken ct = default);

    Task<IReadOnlyList<ConversationSummary>> ListAsync(CancellationToken ct = default);
    Task<Conversation> ContinueAsync(string conversationId, string userMessage, CancellationToken ct = default);
    Task<IReadOnlyList<Message>> ReplayAsync(string conversationId, CancellationToken ct = default);
    Task SuspendForegroundToBackgroundAsync(CancellationToken ct = default);

    BackgroundSession? FindBackgroundSession(SessionId conversationId);
}
