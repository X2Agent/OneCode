namespace OneCode.Core.Hooks;

/// <summary>
/// 钩子数据载荷——传递给钩子处理器的完整上下文
/// </summary>
public sealed record HookPayload
{
    public HookEvent Event { get; init; }
    public string? SessionId { get; init; }
    public string? TranscriptPath { get; init; }
    public string? Cwd { get; init; }
    public string? ToolName { get; init; }
    public JsonElement? ToolInput { get; init; }
    public string? ToolUseId { get; init; }
    public object? ToolResponse { get; init; }
    public string? ToolError { get; init; }
    public bool IsInterrupt { get; init; }
    public string? UserMessage { get; init; }
    public string? AgentId { get; init; }
    public string? AgentType { get; init; }
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
}
