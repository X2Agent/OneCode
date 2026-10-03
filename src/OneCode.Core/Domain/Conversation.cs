namespace OneCode.Core.Domain;

/// <summary>
/// Represents a conversation session.
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

    /// <summary>
    /// 显示标题：用户显式命名优先；否则取首条非 meta、非空用户消息的首行，
    /// 按字符数截断到 <paramref name="maxChars"/> 并追加省略号；
    /// 两者皆无（如尚未有消息的新会话）返回 null。
    /// </summary>
    public string? DisplayTitle(int maxChars = 40)
    {
        if (!string.IsNullOrWhiteSpace(Name)) return Name;
        var firstLine = Messages
            .OfType<UserMessage>()
            .Where(m => !m.IsMeta && !string.IsNullOrWhiteSpace(m.Content))
            .Select(m => m.Content.TrimStart())
            .FirstOrDefault()
            ?.Split('\n', 2)[0]
            .TrimEnd();
        if (string.IsNullOrEmpty(firstLine)) return null;
        return firstLine.Length <= maxChars ? firstLine : firstLine[..maxChars] + "…";
    }
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
