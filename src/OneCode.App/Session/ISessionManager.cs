using OneCode.Core.Session;
using Microsoft.Extensions.AI;

namespace OneCode.App.Session;

/// <summary>
/// 会话管理抽象：创建、恢复、持久化与列表查询。
/// 聚合了若干窄读接口，供需要完整能力的消费方使用。
/// 只需读取能力时，应优先依赖 <see cref="ISessionConversationAccess"/> 或
/// <see cref="ISessionWorkingDirectory"/>。
/// </summary>
public interface ISessionManager : ISessionConversationAccess, ISessionChatHistoryReader, ISessionWorkingDirectory, IAsyncDisposable
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

    Task SaveAsync(CancellationToken ct = default);
    Task PersistTranscriptAsync(CancellationToken ct = default);

    Task<Conversation> EnsureActiveSessionAsync(ConversationOptions options, CancellationToken ct = default);
    Task<Conversation> CreateAsync(ConversationOptions options, CancellationToken ct = default);
    void SetForegroundMode(string mode);
    Task<Conversation?> ResumeAsync(string conversationId, CancellationToken ct = default);
    Task CloseAsync(CancellationToken ct = default);
    Task CloseAsync(string reason, CancellationToken ct = default);
    Task<Conversation?> SwitchToSessionAsync(string conversationId, CancellationToken ct = default);
    Task<Conversation> BackgroundCurrentAndCreateNewAsync(ConversationOptions options, CancellationToken ct = default);
    Task<bool> CloseBackgroundSessionAsync(string conversationId, CancellationToken ct = default);

    Task<IReadOnlyList<ConversationSummary>> ListAsync(CancellationToken ct = default);
    Task<Conversation> ContinueAsync(string conversationId, string userMessage, CancellationToken ct = default);
    Task SuspendForegroundToBackgroundAsync(CancellationToken ct = default);

    BackgroundSession? FindBackgroundSession(SessionId conversationId);
}
