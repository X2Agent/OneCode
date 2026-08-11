namespace OneCode.Core.Permissions;

/// <summary>
/// Immutable approval request shared by Main, Worker, Team, TUI and headless
/// execution paths.
/// </summary>
public sealed record ApprovalRequest(
    string RequestId,
    string ToolName,
    string? ToolInput = null,
    string? AgentName = null,
    string? ConversationId = null,
    string? Reason = null);
