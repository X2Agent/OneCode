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
    /// 运行 Team 计划审批门禁（RequestPortGate 委托）：
    /// RequestPort 挂起、决策收集与 ExternalResponse 恢复收敛于
    /// <c>RequestPortGate</c>。返回包含 ApprovalGranted 的最终结果；
    /// 调用方负责判断是否批准。
    /// </summary>
    private Task<TeamApprovalWorkflowResult> RunApprovalGateAsync(
        string teamName,
        TeamRunId runId,
        TeamConfig config,
        string modelId,
        ImplementationPlan plan,
        Action<OrchestrationEvent>? eventSink,
        CancellationToken ct)
        => _approvalGate.DecideAsync(teamName, runId, config, modelId, plan, eventSink, ct);
}
