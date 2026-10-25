using OneCode.Infrastructure;
using OneCode.Core.Config;

namespace OneCode.App.Services.Interaction;

/// <summary>
/// 审批提问端口的交互式实现：把审批请求交给前端通道并等待决策。
/// 无接收者时拒绝——审批是危险操作的唯一闸门，必须 fail-closed。
/// 等待超过宽限仍未决策时发桌面通知：终端在后台时用户看不见审批卡，靠 OS toast 抢回注意力。
/// </summary>
public sealed class InteractionApprovalPromptSink(
    IInteractionDispatcher dispatcher,
    INotifierService notifier,
    IConfigManager configManager,
    TimeSpan? pendingNotifyGrace = null) : IApprovalPromptSink
{
    /// <summary>默认审批挂起通知宽限：正盯着终端的用户会在宽限内响应，超时说明终端大概率在后台。</summary>
    private static readonly TimeSpan DefaultPendingNotifyGrace = TimeSpan.FromSeconds(30);

    private readonly TimeSpan _pendingNotifyGrace = pendingNotifyGrace ?? DefaultPendingNotifyGrace;

    public async Task<ApprovalDecision> AskAsync(ApprovalRequest request, CancellationToken ct = default)
    {
        var prompt = ApprovalInteractionRequest.FromCore(request);

        using var notifyCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var pendingNotify = NotifyIfStillPendingAsync(request, notifyCts.Token);
        try
        {
            return await InteractionAwaiter.WaitAsync(
                dispatcher, prompt, prompt.ResponseSource, ApprovalDecision.Deny, ct).ConfigureAwait(false);
        }
        finally
        {
            // 响应或取消到达即撤销挂起通知：宽限未到时 Task.Delay 被取消，通知静默不发出。
            notifyCts.Cancel();
            await pendingNotify.ConfigureAwait(false);
        }
    }

    private async Task NotifyIfStillPendingAsync(ApprovalRequest request, CancellationToken ct)
    {
        try
        {
            await Task.Delay(_pendingNotifyGrace, ct).ConfigureAwait(false);
            if (configManager.Current.Effective.NotificationsEnabled == false) return;

            var reason = string.IsNullOrEmpty(request.Reason) ? "" : $"（{request.Reason}）";
            await notifier.SendNotificationAsync(
                "OneCode 审批等待中",
                $"工具 {request.ToolName} 已等待批准超过 {_pendingNotifyGrace.TotalSeconds:F0} 秒{reason}，请回到终端处理。",
                ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 宽限内已响应/取消——无需通知。
        }
    }
}
