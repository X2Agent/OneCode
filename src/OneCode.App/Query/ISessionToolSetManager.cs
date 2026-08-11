namespace OneCode.App.Query;

/// <summary>
/// Per-conversation tool activation state shared by <see cref="ChatService"/> and tool search.
/// </summary>
public interface ISessionToolSetManager
{
    SessionToolSet GetOrCreate(string conversationId);

    bool Remove(string conversationId);

    bool TryActivate(string toolName);

    bool IsActivated(string toolName);
}
