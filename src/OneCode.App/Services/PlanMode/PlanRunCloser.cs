using OneCode.App.Services.Compact;
using OneCode.App.Session;
using OneCode.Core.PlanMode;

namespace OneCode.App.Services.PlanMode;

/// <summary>
/// Plan run 离场后对计划工作流的收口：run 正常结束 → 推进到待审批并发布审批卡；run 被取消 → 工作流置为已取消。
/// 对工作流不存在、已终态、或不属于该 run 的情形幂等 no-op。
/// </summary>
/// <remarks>
/// 进程内的 run 收尾（<c>BuildRunGate</c>）与重启后的孤儿对账（<c>PlanExecutionRecoveryService</c>）
/// 共用这一份实现：两条路径对「什么样的计划可以审批」必须给出同一个答案。
/// </remarks>
public sealed class PlanRunCloser(
    ISessionManager sessionManager,
    IToolProtocolValidator toolProtocolValidator,
    IPlanWorkflowApplicationService planWorkflow,
    PlanCardPublisher? planCardPublisher = null)
{
    /// <summary>
    /// run 自行结束（含输出被拦截、运行异常）：<see cref="PlanWorkflowState.FinalizingPlanRun"/> 的计划推进到待审批。
    /// 计划已提交落盘，run 收尾失败不应让用户失去审批机会；转录协议是否完整由
    /// <see cref="PlanRunCompletedEvent.ProtocolValid"/> 判定。
    /// </summary>
    public async Task FinishAsync(
        SessionId sessionId,
        string agentRunId,
        bool hasOpenToolBatch,
        CancellationToken ct)
    {
        var workflow = await planWorkflow.GetAsync(sessionId, ct).ConfigureAwait(false);
        if (workflow is null
            || workflow.State != PlanWorkflowState.FinalizingPlanRun
            || !string.Equals(workflow.ActiveRunId, agentRunId, StringComparison.Ordinal))
        {
            return;
        }

        var protocolValid = !hasOpenToolBatch
            && toolProtocolValidator.Validate(sessionManager.GetChatHistory(sessionId)).IsValid;
        await planWorkflow.HandleRunEventAsync(
            new PlanRunCompletedEvent(
                sessionId,
                workflow.Id,
                agentRunId,
                protocolValid,
                DateTimeOffset.UtcNow),
            ct).ConfigureAwait(false);

        if (!protocolValid || planCardPublisher is null)
            return;

        var awaiting = await planWorkflow.GetAsync(sessionId, ct).ConfigureAwait(false);
        if (awaiting?.State == PlanWorkflowState.AwaitingApproval
            && awaiting.SubmittedRevision is not null)
        {
            planCardPublisher.Publish(awaiting);
        }
    }

    /// <summary>用户中止 run：未终态且属于该 run 的工作流置为已取消。</summary>
    public async Task CancelAsync(SessionId sessionId, string agentRunId, CancellationToken ct)
    {
        var workflow = await planWorkflow.GetAsync(sessionId, ct).ConfigureAwait(false);
        if (workflow is null
            || workflow.State is PlanWorkflowState.Completed or PlanWorkflowState.Failed or PlanWorkflowState.Cancelled)
        {
            return;
        }
        if (workflow.ActiveRunId is not null
            && !string.Equals(workflow.ActiveRunId, agentRunId, StringComparison.Ordinal))
        {
            return;
        }

        await planWorkflow.CancelAsync(new CancelPlanCommand(
            $"cancel-run-{agentRunId}",
            sessionId,
            workflow.Id,
            workflow.Version,
            "Plan agent run was cancelled."), ct).ConfigureAwait(false);
    }
}
