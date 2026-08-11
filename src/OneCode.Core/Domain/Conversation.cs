namespace OneCode.Core.Domain;

/// <summary>
/// Represents a conversation session.
/// Renamed to Conversation to avoid conflict with OneCode.App.Session namespace.
/// </summary>
public sealed class Conversation
{
    public SessionId Id { get; init; } = SessionId.NewId();
    public string Name { get; set; } = string.Empty;
    public string WorkingDirectory { get; set; } = string.Empty;
    public string Model { get; set; } = "";
    public ConversationStatus Status { get; set; }
    public List<Message> Messages { get; } = new();
    public TokenUsage TotalUsage { get; set; } = new(0, 0);
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset LastActivityAt { get; set; }
    public string? Branch { get; set; }
    public Dictionary<string, object> Metadata { get; } = new();
}

public enum ConversationStatus
{
    Active,
    Paused,
    Completed,
    Failed
}

/// <summary>
/// Options for creating a new session.
/// </summary>
public sealed record ConversationOptions(
    string WorkingDirectory,
    string? Model = null,
    string? Name = null,
    string? ResumeConversationId = null);
