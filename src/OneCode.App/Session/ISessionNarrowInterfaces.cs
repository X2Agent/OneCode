using Microsoft.Extensions.AI;

namespace OneCode.App.Session;

/// <summary>
/// Read-only access to foreground/active conversations.
/// The most widely consumed narrow interface — 10+ consumers need only this.
/// </summary>
public interface ISessionConversationAccess
{
    Conversation? ForegroundConversation { get; }
    Conversation? GetConversation(SessionId conversationId);
}

/// <summary>
/// Session chat history read access — the narrow surface the MAF chat history bridge needs
/// (<c>TranscriptChatHistoryProvider</c>). Implemented by <see cref="SessionManager"/>.
/// </summary>
public interface ISessionChatHistoryReader
{
    IReadOnlyList<ChatMessage> GetChatHistory(SessionId conversationId);
}

/// <summary>
/// Session working directory access and mutation.
/// </summary>
public interface ISessionWorkingDirectory
{
    string WorkingDirectory { get; }
    Task ChangeWorkingDirectoryAsync(string newCwd, CancellationToken ct = default);
}
