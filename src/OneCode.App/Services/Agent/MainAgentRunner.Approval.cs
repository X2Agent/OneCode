using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OneCode.App.Query;

namespace OneCode.App.Services.Agent;

/// <summary>
/// <see cref="MainAgentRunner"/> 的工具审批处理。
///
/// 完全事件驱动：审批请求通过 <see cref="ApprovalRequestEvent"/> 推送到 channel，
/// TUI 消费 <see cref="OneCode.App.Tui.TuiApprovalRequest"/> 后自主渲染审批组件，
/// 通过 <see cref="ApprovalRequestEvent.ResponseSource"/> 回传 <see cref="ApprovalDecision"/>。
/// Main 路径完全脱离同步阻塞调用。
/// </summary>
public partial class MainAgentRunner
{
    /// <summary>
    /// 处理单个 MAF ToolApprovalRequestContent（流式 + 非流式统一路径）。
    /// 推送 <see cref="ApprovalRequestEvent"/> 到 channel，await ResponseSource 回传决策。
    /// </summary>
    internal async Task<AIContent> HandleToolApprovalAsync(
        ToolApprovalRequestContent request,
        IApprovalBroker? broker,
        CancellationToken ct)
    {
        var functionCall = request.ToolCall as FunctionCallContent;
        var toolName = functionCall?.Name ?? "unknown";
        var toolInput = functionCall?.Arguments is not null
            ? JsonSerializer.SerializeToElement(functionCall.Arguments)
            : JsonSerializer.SerializeToElement(new { });

        _logger.LogDebug("ToolApprovalRequest: tool={Tool}, requestId={Id}", toolName, request.RequestId);

        // 受控 Build（SuppressToolApproval）等无审批通道的 run 也会漏出审批请求
        // （如 execute_code 恒带 AlwaysRequire 审批边界，FICC 拦截在权限层之前）。
        // 无人应答时必须 fail-closed 拒绝本次调用——抛异常会让整个 run 失败，
        // 进而回滚事务内全部文件改动。
        if (broker is null)
        {
            _logger.LogWarning(
                "Tool approval request for {Tool} (request {RequestId}) surfaced without an approval broker; denying",
                toolName,
                request.RequestId);
            return request.CreateResponse(
                approved: false,
                $"Tool '{toolName}' requires approval but no approval channel is available in this run.");
        }

        var decision = await broker.RequestAsync(
            new ApprovalRequest(
                RequestId: request.RequestId,
                ToolName: toolName,
                ToolInput: toolInput.GetRawText()),
            ct).ConfigureAwait(false);

        _logger.LogDebug("ToolApprovalResponse: tool={Tool}, decision={Decision}", toolName, decision);

        return decision switch
        {
            ApprovalDecision.AllowOnce => request.CreateResponse(true, $"User approved {toolName}"),
            ApprovalDecision.AllowAlways => request.CreateAlwaysApproveToolResponse($"User always-approved {toolName}"),
            // 本次对话全部允许：仅批准当前调用，不注册 MAF standing rule——
            // run 内后续调用由 ApprovalBroker 自动放行，跨 run 由 PermissionModeProvider
            // 运行时覆盖（BypassPermissions，不持久化）生效。
            ApprovalDecision.AllowAllConversation =>
                request.CreateResponse(true, "User allowed all tool calls for this conversation."),
            _ => request.CreateResponse(false, "User denied."),
        };
    }
}
