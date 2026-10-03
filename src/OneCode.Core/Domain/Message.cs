namespace OneCode.Core.Domain;

/// <summary>
/// 对话消息基类
/// </summary>
public abstract record Message(string Id, MessageRole Role, DateTimeOffset Timestamp);

public enum MessageRole
{
    User,
    Assistant,
    System,
    Tool,
}

/// <summary>
/// 用户消息
/// </summary>
public sealed record UserMessage(
    string Id,
    string Content,
    DateTimeOffset Timestamp,
    string? Uuid = null,
    bool IsMeta = false) : Message(Id, MessageRole.User, Timestamp);

/// <summary>
/// 助手消息（AI 的回复）
/// </summary>
public sealed record AssistantMessage(
    string Id,
    IReadOnlyList<ContentBlock> Content,
    DateTimeOffset Timestamp,
    TokenUsage? TokenUsage = null,
    string? Uuid = null) : Message(Id, MessageRole.Assistant, Timestamp);

/// <summary>
/// 系统消息
/// </summary>
public sealed record SystemMessage(
    string Id,
    string Content,
    DateTimeOffset Timestamp) : Message(Id, MessageRole.System, Timestamp);

/// <summary>
/// 工具结果消息
/// </summary>
public sealed record ToolResultMessage(
    string Id,
    string ToolUseId,
    string ToolName,
    string Content,
    bool IsError,
    DateTimeOffset Timestamp,
    string? Uuid = null) : Message(Id, MessageRole.Tool, Timestamp);

/// <summary>
/// 内容块基类
/// </summary>
public abstract record ContentBlock;

public sealed record TextBlock(string Text) : ContentBlock;

public sealed record ToolUseBlock(
    string Id,
    string Name,
    string Input) : ContentBlock;

/// <summary>
/// 思考内容块——AI 的内部推理过程
/// </summary>
public sealed record ThinkingBlock(
    string Thinking) : ContentBlock;

/// <summary>
/// 红acted 思考内容块——用户不可见的思考
/// </summary>
public sealed record RedactedThinkingBlock(
    string Data) : ContentBlock;

/// <summary>
/// Token 使用统计
/// </summary>
public sealed record TokenUsage(
    int InputTokens,
    int OutputTokens,
    int CacheReadTokens = 0,
    int CacheWriteTokens = 0)
{
    public int TotalTokens => InputTokens + OutputTokens;
}
