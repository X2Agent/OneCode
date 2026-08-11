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
    private async Task<AIContent> HandleToolApprovalAsync(
        ToolApprovalRequestContent request,
        IApprovalBroker broker,
        CancellationToken ct)
    {
        var functionCall = request.ToolCall as FunctionCallContent;
        var toolName = functionCall?.Name ?? "unknown";
        var toolInput = functionCall?.Arguments is not null
            ? JsonSerializer.SerializeToElement(functionCall.Arguments)
            : JsonSerializer.SerializeToElement(new { });

        _logger.LogDebug("ToolApprovalRequest: tool={Tool}, requestId={Id}", toolName, request.RequestId);

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
            _ => request.CreateResponse(false, "User denied."),
        };
    }
}
