namespace OneCode.Core.Permissions;

/// <summary>
/// 不可变审批请求，供主 agent、子代理、各前端与无头执行路径共用。
/// </summary>
public sealed record ApprovalRequest(
    string RequestId,
    string ToolName,
    string? ToolInput = null,
    string? AgentName = null,
    string? ConversationId = null,
    string? Reason = null);
