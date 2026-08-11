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

    public static ApprovalBroker ForQuery(
        ChannelWriter<object> writer,
        ILogger<ApprovalBroker>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(writer);

        return new ApprovalBroker(async (request, ct) =>
        {
            var evt = new ApprovalRequestEvent(
                request.RequestId,
                request.ToolName,
                request.ToolInput,
                request.Reason);

            await writer.WriteAsync(evt, ct).ConfigureAwait(false);
            return await evt.ResponseSource.Task.WaitAsync(ct).ConfigureAwait(false);
        }, logger);
    }

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
        catch (OperationCanceledException)
        {
            return ApprovalDecision.Deny;
        }
        catch (Exception ex)
        {
            _logger?.LogError(
                ex,
                "Approval broker failed closed for tool {ToolName}, request {RequestId}",
                request.ToolName,
                request.RequestId);
            return ApprovalDecision.Deny;
        }
    }
}
