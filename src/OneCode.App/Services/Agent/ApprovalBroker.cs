using System.Threading.Channels;
using OneCode.App.Query;
using OneCode.Core.Coordinator;

namespace OneCode.App.Services.Agent;

/// <summary>
/// Bridges the shared Core approval contract to an application event stream.
/// The broker owns timeout/cancellation behavior; UI code only completes the
/// response source carried by the emitted event.
/// </summary>
public sealed class ApprovalBroker : IApprovalBroker
{
    private readonly Func<ApprovalRequest, CancellationToken, Task<ApprovalDecision>> _request;
    private readonly ILogger<ApprovalBroker>? _logger;

    private ApprovalBroker(
        Func<ApprovalRequest, CancellationToken, Task<ApprovalDecision>> request,
        ILogger<ApprovalBroker>? logger = null)
    {
        _request = request;
        _logger = logger;
    }

    public Task<ApprovalDecision> RequestAsync(
        ApprovalRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return RequestCoreAsync(request, ct);
    }

    /// <summary>
    /// Main 路径 broker。在 TUI 审批请求下发前触发 Notification hook
    /// （matcher=permission_prompt）；hook 失败/取消仅记录，不影响审批链路。
    /// </summary>
    public static ApprovalBroker ForQuery(
        ChannelWriter<object> writer,
        Func<string, CancellationToken, Task>? onPermissionPrompt = null,
        ILogger<ApprovalBroker>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(writer);

        return new ApprovalBroker(async (request, ct) =>
        {
            if (onPermissionPrompt is not null)
            {
                try
                {
                    await onPermissionPrompt(request.ToolName, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // Notification hook 无阻断语义；执行器内部已记录日志，此处保证审批链路可靠
                    logger?.LogDebug(ex, "Permission prompt hook failed for tool {ToolName}", request.ToolName);
                }
            }

            var evt = new ApprovalRequestEvent(
                request.RequestId,
                request.ToolName,
                request.ToolInput,
                request.Reason);

            await writer.WriteAsync(evt, ct).ConfigureAwait(false);
            return await evt.ResponseSource.Task.WaitAsync(ct).ConfigureAwait(false);
        }, logger);
    }

    /// <summary>
    /// Team 路径 broker（R4）：由 <c>TeamWorkflowRunner.BridgeToolApprovalAsync</c> 在成员审批请求
    /// 以 <c>RequestInfoEvent</c> 浮出时调用，推送 <c>OrchestrationEvent.ApprovalRequest</c> 并等待
    /// TUI 决策；决策经 <c>SendResponseAsync</c> 送回同一工作流。
    /// 「总是允许」经该桥接当前只提供单次批准（standing rule 包装未验证通过端口响应类型校验）。
    /// </summary>
    public static ApprovalBroker ForTeam(
        string agentName,
        Action<OrchestrationEvent>? eventSink,
        ILogger<ApprovalBroker>? logger = null)
    {
        return new ApprovalBroker(async (request, ct) =>
        {
            if (eventSink is null)
                return ApprovalDecision.Deny;

            var evt = new OrchestrationEvent.ApprovalRequest(
                request with { AgentName = string.IsNullOrEmpty(request.AgentName) ? agentName : request.AgentName });
            eventSink(evt);

            // 与 Main 路径一致：只依赖 ct 取消，不设独立超时。
            // 审批的本质是等待用户决策，固定超时会在用户思考时错误地自动 Deny。
            // TUI 不消费事件的场景属于 bug，应修 TUI 而非用超时掩盖。
            return await evt.ResponseSource.Task.WaitAsync(ct).ConfigureAwait(false);
        }, logger);
    }

    private async Task<ApprovalDecision> RequestCoreAsync(
        ApprovalRequest request,
        CancellationToken ct)
    {
        try
        {
            var decision = await _request(request, ct).ConfigureAwait(false);
            return decision;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Caller cancellation is not a decision. Returning Deny here would make "the user stopped
            // the run" indistinguishable from "the user said no", and the run would keep unwinding as
            // if a policy judgement had been made. The token was cancelled, so the run is over either
            // way — propagate and let the caller observe the cancellation it asked for.
            _logger?.LogDebug(
                "Approval wait cancelled for tool {ToolName}, request {RequestId}",
                request.ToolName,
                request.RequestId);
            throw;
        }
        catch (OperationCanceledException)
        {
            // The wait timed out without the caller cancelling (e.g. an internal timeout).
            // There is no user decision to honour, so fail closed.
            _logger?.LogWarning(
                "Approval wait ended without a decision for tool {ToolName}, request {RequestId}; denying",
                request.ToolName,
                request.RequestId);
            return ApprovalDecision.Deny;
        }
        catch (Exception ex)
        {
            // UI failure: the user was never asked, so the safe outcome is to refuse the tool.
            _logger?.LogError(
                ex,
                "Approval broker failed closed for tool {ToolName}, request {RequestId}",
                request.ToolName,
                request.RequestId);
            return ApprovalDecision.Deny;
        }
    }
}
