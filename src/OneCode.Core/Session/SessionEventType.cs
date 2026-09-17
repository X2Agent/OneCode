namespace OneCode.Core.Session;

/// <summary>会话事件类型。</summary>
public enum SessionEventType
{
    SessionStarted,
    SessionSnapshot,
    TurnStarted,
    TurnEnded,
    StepStarted,
    StepEnded,
    UserMessage,
    SystemMessage,
    AssistantMessage,
    AssistantAttempt,
    ToolCall,
    ToolResult,
    MessagesReplaced,
    AttachmentMessage,
    TombstoneMessage,
    CompactionStarted,
    CompactionSummary,
    CompactionPruned,
    CompactionEnded,
    HookInvoked,
    HookResult,
    GoalChanged,
}

/// <summary>
/// <see cref="SessionEventType"/> 在 JSONL 事件文件中的字面量（枚举名经
/// <see cref="System.Text.Json.Serialization.JsonStringEnumConverter"/> +
/// <see cref="System.Text.Json.JsonNamingPolicy.SnakeCaseLower"/> 序列化）。
/// </summary>
/// <remarks>
/// 读取事件文件的非反序列化消费者（如 <c>InsightsCommand</c>）必须用这些常量比对
/// <c>type</c> 字段，避免出现硬编码字符串与序列化策略漂移。
/// </remarks>
public static class SessionEventTypes
{
    public const string Started = "session_started";
    public const string Snapshot = "session_snapshot";
    public const string UserMessage = "user_message";
    public const string AssistantMessage = "assistant_message";
}