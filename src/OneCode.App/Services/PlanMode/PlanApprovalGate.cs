using OneCode.App.Tui;
using OneCode.Core.PlanMode;

namespace OneCode.App.Services.PlanMode;

/// <summary>审批决策的结果投影：决策后的工作流快照（供调用方发布到渲染层）。</summary>
public sealed record DecisionOutcome(PlanWorkflow Workflow);

/// <summary>
/// Plan 审批聚合门（控制面并入）：
/// TUI 决策面板与 headless 决策的唯一入口——状态校验与命令构造
/// （<see cref="IPlanWorkflowApplicationService"/>）、投影发布（<see cref="PlanCardPublisher"/>）
/// 与批准后的 Build 派发（<see cref="IPlanAgentRunDispatcher"/>）收敛于此，
/// 决策面板只负责键盘交互。
/// 语义保留 Plan 现状（聚合 <c>AwaitingApproval</c> 态 + 发布投影），
/// 不与其余模式的审批机制强行统一。
/// </summary>
/// <param name="workflowService">Plan 工作流应用服务（状态机唯一实现）。</param>
/// <param name="publisher">Plan 卡片投影发布器（TUI 订阅渲染）。</param>
/// <param name="runDispatcher">批准后的 Build 执行派发器。</param>
/// <param name="logger">日志。</param>
public sealed class AggregateApprovalGate(
    IPlanWorkflowApplicationService workflowService,
    PlanCardPublisher publisher,
    IPlanAgentRunDispatcher runDispatcher,
    ILogger<AggregateApprovalGate> logger)
{
    /// <summary>
    /// 执行用户决策（批准/拒绝/请求修订）并恢复工作流：幂等（重复 CommandId 返回
    /// 既有状态）、版本冲突/非法状态抛出（fail-closed，由展示层捕获呈现）。
    /// 成功路径上投影已发布；批准时同步切换 Build 执行（派发器幂等去重并发启动）。
    /// </summary>
    public async Task<DecisionOutcome> DecideAsync(
        InteractiveSession session,
        PlanCardDecision decision,
        CancellationToken ct = default)
    {
        var outcome = await workflowService.DecideAsync(session, decision, ct).ConfigureAwait(false);
        publisher.Publish(outcome.Workflow);
        if (decision == PlanCardDecision.Approve)
        {
            logger.LogInformation(
                "Plan {PlanId} approved by user, dispatching build run {RunId}",
                outcome.Workflow.Id,
                outcome.Workflow.ActiveRunId);
            await runDispatcher.StartBuildAsync(session, outcome.Workflow, ct).ConfigureAwait(false);
        }

        return outcome;
    }
}
