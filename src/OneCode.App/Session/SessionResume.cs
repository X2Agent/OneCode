namespace OneCode.App.Session;

public sealed record SessionResume(
    SessionId SessionId,
    IReadOnlyList<Message> Messages,
    InterruptionState InterruptionState,
    DateTimeOffset LastModified,
    string? Title,
    int MessageCount);

public enum InterruptionState
{
    None,
    InterruptedPrompt,
    InterruptedTurn,
}
