namespace OneCode.App.Tools;

/// <summary>
/// Narrow interface for shell executor cleanup on session close.
/// Decouples SessionManager from the full ConversationShellExecutorManager concrete type.
/// </summary>
public interface IShellExecutorCleanup
{
    Task ReleaseAsync(SessionId conversationId);
}
