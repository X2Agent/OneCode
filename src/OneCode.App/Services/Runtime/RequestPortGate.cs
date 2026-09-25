using Microsoft.Agents.AI.Workflows;
using OneCode.App.Services.Coordinator;
using OneCode.Core.Coordinator;

namespace OneCode.App.Services.Runtime;

/// <summary>
/// Team 计划审批门（RequestPortGate）：首次调用挂起于 MAF RequestPort，
/// 经共享澄清交互通道收集用户决策后投递 ExternalResponse 恢复同一执行世代；
/// 挂起存活于 checkpoint，崩溃后可从持久化门恢复。
/// internal：依赖 Team 审批工作流宿主（internal）。
/// </summary>
internal sealed class RequestPortGate(
    TeamApprovalWorkflowHost approvalWorkflowHost,
    IClarificationInteractionService clarificationInteraction)
{
    /// <summary>
    /// 运行 Team 计划审批门禁：构造 approvalInput，首次调用挂起于 RequestPort，
    /// 通过 AskAsync 获取用户审批决策后投递 ExternalResponse 恢复。
    /// 返回包含 ApprovalGranted 的最终结果；调用方负责判断是否批准。
    /// </summary>
    public async Task<TeamApprovalWorkflowResult> DecideAsync(
        string teamName,
        TeamRunId runId,
        TeamConfig config,
        string modelId,
        ImplementationPlan plan,
        Action<OrchestrationEvent>? eventSink,
        CancellationToken ct)
    {
        var approvalInput = new TeamPlanApprovalInput(
            runId.Value,
            teamName,
            plan.Summary,
            plan.Tasks.Select(t => t.Title).ToList(),
            plan.RequiredGates.Where(g => g.Required).Select(g => g.Description).ToList());

        var approval = await approvalWorkflowHost.RunApprovalAsync(
            teamName, runId, config, modelId, approvalInput,
            new JsonSerializerOptions(), ct: ct).ConfigureAwait(false);

        if (approval.PendingRequest is { } pending)
        {
            // Notify TUI of plan approval card (display-only, no TaskCompletionSource).
            eventSink?.Invoke(new OrchestrationEvent.TeamPlanApprovalRequest(
                runId, teamName, plan.Summary,
                plan.Tasks.Select(t => t.Title).ToList(),
                plan.RequiredGates.Where(g => g.Required).Select(g => g.Description).ToList()));

            var decision = await clarificationInteraction.AskAsync(
                $"团队 {teamName} 计划审批",
                [$"执行方案：{plan.Summary}\n任务数：{plan.Tasks.Count}\n批准执行？"],
                confirmationOnly: true,
                ct: ct).ConfigureAwait(false);
            var approved = !decision.IsCancelled;
            // 决策回显：让用户在会话记录中看到自己批准/取消了计划。
            eventSink?.Invoke(new OrchestrationEvent.TeamUserResponse(
                teamName, approved ? "已批准执行计划" : "已取消，不执行"));

            var response = BuildApprovalResponse(pending.PortId, pending.RequestId, approved);
            approval = await approvalWorkflowHost.RunApprovalAsync(
                teamName, runId, config, modelId, approvalInput,
                new JsonSerializerOptions(),
                externalResponse: response,
                ct: ct).ConfigureAwait(false);
        }

        return approval;
    }

    /// <summary>构造 MAF ExternalResponse 以恢复 Team 计划审批工作流（RequestPort 决策投递）。</summary>
    private static ExternalResponse BuildApprovalResponse(
        string portId, string requestId, bool approved) =>
        new(
            new Microsoft.Agents.AI.Workflows.Checkpointing.RequestPortInfo(
                new Microsoft.Agents.AI.Workflows.Checkpointing.TypeId(typeof(TeamPlanApprovalInput)),
                new Microsoft.Agents.AI.Workflows.Checkpointing.TypeId(typeof(TeamPlanApprovalDecision)),
                portId),
            requestId,
            new Microsoft.Agents.AI.Workflows.PortableValue(
                new TeamPlanApprovalDecision(approved)));
}