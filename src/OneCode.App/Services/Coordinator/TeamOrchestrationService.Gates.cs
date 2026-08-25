using OneCode.Core.Coordinator;

namespace OneCode.App.Services.Coordinator;

/// <summary>
/// Team 编排的用户交互门禁：澄清门（MAF RequestPort 挂起 → AskAsync 收集回答 → ExternalResponse 恢复）
/// 与计划审批门（同机制，confirmationOnly）。以 partial 拆分——两个门禁方法强耦合共享
/// _clarificationWorkflowHost/_approvalWorkflowHost/_clarificationInteraction 私有字段，
/// 符合 App/AGENTS.md 的 partial 规则②；工作流执行与生命周期保留在主文件。
/// </summary>
public sealed partial class TeamOrchestrationService
{
    /// <summary>
    /// 运行 Team 澄清门禁：首次调用挂起于 MAF RequestPort，通过 AskAsync 获取用户回答后投递
    /// ExternalResponse 恢复。返回包含 Answer 的最终结果；调用方负责判断 Answer 是否为空。
    /// </summary>
    private async Task<TeamClarificationResult> RunClarificationGateAsync(
        string teamName,
        TeamRunId runId,
        TeamConfig config,
        string modelId,
        IReadOnlyList<string> questions,
        string goalForEvent,
        Action<OrchestrationEvent>? eventSink,
        CancellationToken ct)
    {
        var clarificationInput = new TeamClarificationInput(runId.Value, teamName, questions);
        var clarification = await _clarificationWorkflowHost.RunAsync(
            teamName, runId, config, modelId, clarificationInput,
            new JsonSerializerOptions(), ct: ct).ConfigureAwait(false);

        if (clarification.PendingRequest is { } pending)
        {
            eventSink?.Invoke(new OrchestrationEvent.TeamClarificationRequest(
                runId, teamName, goalForEvent, questions));
            var answer = await _clarificationInteraction.AskAsync(
                "团队任务需要补充信息", questions, ct: ct).ConfigureAwait(false);
            // 回答回显：用户在澄清向导中的回答写入会话记录，否则 TUI 上看不到用户答了什么。
            eventSink?.Invoke(new OrchestrationEvent.TeamUserResponse(
                teamName, answer.Response ?? string.Empty));
            var response = BuildClarificationResponse(
                pending.PortId, pending.RequestId, answer.Response ?? string.Empty);
            clarification = await _clarificationWorkflowHost.RunAsync(
                teamName, runId, config, modelId, clarificationInput,
                new JsonSerializerOptions(), response, ct).ConfigureAwait(false);
        }

        return clarification;
    }

    /// <summary>
    /// 运行 Team 计划审批门禁：构造 approvalInput，首次调用挂起于 MAF RequestPort，
    /// 通过 AskAsync 获取用户审批决策后投递 ExternalResponse 恢复。
    /// 返回包含 ApprovalGranted 的最终结果；调用方负责判断是否批准。
    /// </summary>
    private async Task<TeamApprovalWorkflowResult> RunApprovalGateAsync(
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

        var approval = await _approvalWorkflowHost.RunApprovalAsync(
            teamName, runId, config, modelId, approvalInput,
            new JsonSerializerOptions(), ct: ct).ConfigureAwait(false);

        if (approval.PendingRequest is { } pending)
        {
            // Notify TUI of plan approval card (display-only, no TaskCompletionSource).
            eventSink?.Invoke(new OrchestrationEvent.TeamPlanApprovalRequest(
                runId, teamName, plan.Summary,
                plan.Tasks.Select(t => t.Title).ToList(),
                plan.RequiredGates.Where(g => g.Required).Select(g => g.Description).ToList()));

            var decision = await _clarificationInteraction.AskAsync(
                $"团队 {teamName} 计划审批",
                [$"执行方案：{plan.Summary}\n任务数：{plan.Tasks.Count}\n批准执行？"],
                confirmationOnly: true,
                ct: ct).ConfigureAwait(false);
            var approved = !decision.IsCancelled;
            // 决策回显：让用户在会话记录中看到自己批准/取消了计划。
            eventSink?.Invoke(new OrchestrationEvent.TeamUserResponse(
                teamName, approved ? "已批准执行计划" : "已取消，不执行"));

            var response = BuildApprovalResponse(pending.PortId, pending.RequestId, approved);
            approval = await _approvalWorkflowHost.RunApprovalAsync(
                teamName, runId, config, modelId, approvalInput,
                new JsonSerializerOptions(),
                externalResponse: response,
                ct: ct).ConfigureAwait(false);
        }

        return approval;
    }
}
