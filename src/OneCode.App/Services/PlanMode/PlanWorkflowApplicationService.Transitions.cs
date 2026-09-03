using OneCode.App.Tui;
using OneCode.Core.PlanMode;

namespace OneCode.App.Services.PlanMode;

/// <summary>
/// <see cref="PlanWorkflowApplicationService"/> 的审批门执行面：
/// 决策的幂等/校验/状态转换仍由本服务唯一实现；门实现（<see cref="AggregateApprovalGate"/>）
/// 只做编排（调用决策 → 发布投影 → 批准后派发），不重复校验规则。
/// </summary>
public sealed partial class PlanWorkflowApplicationService
{
    /// <inheritdoc cref="IPlanWorkflowApplicationService.RequireDecidableAsync"/>
    public async Task<PlanWorkflow> RequireDecidableAsync(SessionId sessionId, CancellationToken ct = default)
    {
        var workflow = await GetAsync(sessionId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"No active plan workflow exists for session '{sessionId}'.");
        if (workflow.State != PlanWorkflowState.AwaitingApproval
            || workflow.SubmittedRevision is null)
        {
            throw new InvalidOperationException(
                $"Plan approval is not available in state '{workflow.State}'.");
        }

        return workflow;
    }

    /// <inheritdoc cref="IPlanWorkflowApplicationService.DecideAsync"/>
    public async Task<DecisionOutcome> DecideAsync(
        InteractiveSession session,
        PlanCardDecision decision,
        CancellationToken ct = default)
    {
        var workflow = await RequireDecidableAsync(
            session.SessionManager.ForegroundConversation?.Id
                ?? throw new InvalidOperationException("Plan decision requires an active conversation."),
            ct).ConfigureAwait(false);
        var commandId = Guid.NewGuid().ToString("N");

        return decision switch
        {
            PlanCardDecision.Approve => new DecisionOutcome(
                (await ApproveAsync(
                    new ApprovePlanCommand(
                        commandId,
                        workflow.SessionId,
                        workflow.Id,
                        workflow.SubmittedRevision!.Value,
                        workflow.Version,
                        "interactive-user"),
                    ct).ConfigureAwait(false)).Workflow),
            PlanCardDecision.Reject => new DecisionOutcome(
                (await RejectAsync(
                    new RejectPlanCommand(
                        commandId,
                        workflow.SessionId,
                        workflow.Id,
                        workflow.SubmittedRevision!.Value,
                        workflow.Version,
                        "Rejected by interactive user."),
                    ct).ConfigureAwait(false)).Workflow),
            PlanCardDecision.Edit => new DecisionOutcome(
                (await RequestEditAsync(
                    new RequestPlanEditCommand(
                        commandId,
                        workflow.SessionId,
                        workflow.Id,
                        workflow.SubmittedRevision!.Value,
                        workflow.Version,
                        "请根据用户反馈修订计划。",
                        []),
                    ct).ConfigureAwait(false)).Workflow),
            _ => throw new ArgumentOutOfRangeException(nameof(decision), decision, "Unknown plan card decision."),
        };
    }
}
