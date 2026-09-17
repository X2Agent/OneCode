using OneCode.Core.Domain;

namespace OneCode.Core.Session;

/// <summary>追加写入的会话事件信封。</summary>
public abstract record SessionEvent(
    SessionId SessionId,
    long Sequence,
    DateTimeOffset OccurredAt,
    SessionEventType Type,
    bool Ignorable = false)
{
    /// <summary>以存储分配的序号创建事件副本。</summary>
    public abstract SessionEvent WithSequence(long sequence);
}

/// <summary>用户消息事件。</summary>
public sealed record UserMessageEvent(
    SessionId SessionId,
    long Sequence,
    DateTimeOffset OccurredAt,
    UserMessage Message) : SessionEvent(SessionId, Sequence, OccurredAt, SessionEventType.UserMessage)
{
    public override SessionEvent WithSequence(long sequence) => this with { Sequence = sequence };
}

/// <summary>系统消息事件。</summary>
public sealed record SystemMessageEvent(
    SessionId SessionId,
    long Sequence,
    DateTimeOffset OccurredAt,
    SystemMessage Message) : SessionEvent(SessionId, Sequence, OccurredAt, SessionEventType.SystemMessage)
{
    public override SessionEvent WithSequence(long sequence) => this with { Sequence = sequence };
}

/// <summary>助手消息事件。</summary>
public sealed record AssistantMessageEvent(
    SessionId SessionId,
    long Sequence,
    DateTimeOffset OccurredAt,
    AssistantMessage Message) : SessionEvent(SessionId, Sequence, OccurredAt, SessionEventType.AssistantMessage)
{
    public override SessionEvent WithSequence(long sequence) => this with { Sequence = sequence };
}

/// <summary>工具结果事件。</summary>
public sealed record ToolResultEvent(
    SessionId SessionId,
    long Sequence,
    DateTimeOffset OccurredAt,
    ToolResultMessage Message) : SessionEvent(SessionId, Sequence, OccurredAt, SessionEventType.ToolResult)
{
    public override SessionEvent WithSequence(long sequence) => this with { Sequence = sequence };
}

/// <summary>通用无消息事件，用于 turn、step、compaction、hook 和 goal 事实记录。</summary>
public sealed record SessionMarkerEvent(
    SessionId SessionId,
    long Sequence,
    DateTimeOffset OccurredAt,
    SessionEventType Type,
    string? Data = null,
    bool Ignorable = false) : SessionEvent(SessionId, Sequence, OccurredAt, Type, Ignorable)
{
    public override SessionEvent WithSequence(long sequence) => this with { Sequence = sequence };
}

/// <summary>附件消息事件。</summary>
public sealed record AttachmentMessageEvent(
    SessionId SessionId,
    long Sequence,
    DateTimeOffset OccurredAt,
    AttachmentMessage Message) : SessionEvent(SessionId, Sequence, OccurredAt, SessionEventType.AttachmentMessage)
{
    public override SessionEvent WithSequence(long sequence) => this with { Sequence = sequence };
}

/// <summary>墓碑消息事件。</summary>
public sealed record TombstoneMessageEvent(
    SessionId SessionId,
    long Sequence,
    DateTimeOffset OccurredAt,
    TombstoneMessage Message) : SessionEvent(SessionId, Sequence, OccurredAt, SessionEventType.TombstoneMessage)
{
    public override SessionEvent WithSequence(long sequence) => this with { Sequence = sequence };
}

/// <summary>会话创建和元数据快照事件。</summary>
public sealed record SessionStartedEvent(
    SessionId SessionId,
    long Sequence,
    DateTimeOffset OccurredAt,
    string Name,
    string WorkingDirectory,
    string Model,
    ConversationStatus Status,
    TokenUsage TotalUsage,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastActivityAt,
    string? Branch,
    IReadOnlyDictionary<string, object>? Metadata = null)
    : SessionEvent(SessionId, Sequence, OccurredAt, SessionEventType.SessionStarted)
{
    public override SessionEvent WithSequence(long sequence) => this with { Sequence = sequence };
}

/// <summary>会话状态变更后的完整元数据快照。</summary>
public sealed record SessionSnapshotEvent(
    SessionId SessionId,
    long Sequence,
    DateTimeOffset OccurredAt,
    string Name,
    string WorkingDirectory,
    string Model,
    ConversationStatus Status,
    TokenUsage TotalUsage,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastActivityAt,
    string? Branch,
    IReadOnlyDictionary<string, object>? Metadata = null)
    : SessionEvent(SessionId, Sequence, OccurredAt, SessionEventType.SessionSnapshot)
{
    public override SessionEvent WithSequence(long sequence) => this with { Sequence = sequence };
}

/// <summary>消息集合被压缩、回滚或其他结构性操作替换的事件。</summary>
public sealed record MessagesReplacedEvent(
    SessionId SessionId,
    long Sequence,
    DateTimeOffset OccurredAt,
    IReadOnlyList<Message> Messages,
    string Reason)
    : SessionEvent(SessionId, Sequence, OccurredAt, SessionEventType.MessagesReplaced)
{
    public override SessionEvent WithSequence(long sequence) => this with { Sequence = sequence };
}