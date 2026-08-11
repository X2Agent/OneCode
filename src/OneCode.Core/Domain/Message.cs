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
    Attachment,
    Tombstone,
}

/// <summary>
/// 用户消息
/// </summary>
public sealed record UserMessage(
    string Id,
    string Content,
    DateTimeOffset Timestamp,
    IReadOnlyList<Attachment>? Attachments = null,
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
    string? Uuid = null,
    bool IsApiErrorMessage = false,
    ApiErrorInfo? ApiError = null) : Message(Id, MessageRole.Assistant, Timestamp);

/// <summary>
/// 系统消息
/// </summary>
public sealed record SystemMessage(
    string Id,
    string Content,
    DateTimeOffset Timestamp,
    SystemMessageType Type = SystemMessageType.Info) : Message(Id, MessageRole.System, Timestamp);

/// <summary>
/// 系统消息类型
/// </summary>
public enum SystemMessageType
{
    Info,
    Warning,
    Error
}

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
/// 附件消息——钩子结果、文件变更通知等非用户输入的附加信息
/// </summary>
public sealed record AttachmentMessage(
    string Id,
    string Content,
    AttachmentType Type,
    DateTimeOffset Timestamp,
    string? SourceToolUseId = null) : Message(Id, MessageRole.Attachment, Timestamp);

public enum AttachmentType
{
    HookResult,
    FileChangedNotification,
    MemoryNotification,
    McpResourceUpdate,
}

/// <summary>
/// 墓碑消息——流式 fallback 时标记孤立消息
///
/// 当流式响应中断或回退时，某些消息可能变得无效。
/// TombstoneMessage 用于标记这些消息，使 UI 可以隐藏或替换它们。
/// </summary>
public sealed record TombstoneMessage(
    string Id,
    string OriginalMessageId,
    string Reason,
    DateTimeOffset Timestamp) : Message(Id, MessageRole.Tombstone, Timestamp);

/// <summary>
/// API 错误信息
/// </summary>
public sealed record ApiErrorInfo(
    string Type,
    string Message,
    int? StatusCode = null);

/// <summary>
/// 内容块基类
/// </summary>
public abstract record ContentBlock;

public sealed record TextBlock(string Text) : ContentBlock;

public sealed record ToolUseBlock(
    string Id,
    string Name,
    string Input,
    string? CacheControl = null) : ContentBlock;

/// <summary>
/// 思考内容块——AI 的内部推理过程
/// </summary>
public sealed record ThinkingBlock(
    string Thinking,
    string? CacheControl = null) : ContentBlock;

/// <summary>
/// 红acted 思考内容块——用户不可见的思考
/// </summary>
public sealed record RedactedThinkingBlock(
    string Data) : ContentBlock;

/// <summary>
/// 附件
/// </summary>
public sealed record Attachment(
    string Type,
    string MediaType,
    string Data);

/// <summary>
/// Token 使用统计
/// </summary>
public sealed record TokenUsage(
    int InputTokens,
    int OutputTokens,
    int CacheReadTokens = 0,
    int CacheWriteTokens = 0,
    decimal? TotalCostUsd = null)
{
    public int TotalTokens => InputTokens + OutputTokens;
}
