using OneCode.App.Services.BuildMode;
using OneCode.Core.Build;
using System.Runtime.CompilerServices;

namespace OneCode.App.Query;

/// <summary>
/// 受控执行前置的迭代器实现：恢复/创建 durable BuildRun，把上层批准的计划
/// （<see cref="QueryStreamRequest.PrescribedBuildPlan"/>，来自 PlanWorkflow 审批卡）
/// 放行进受控 attempt。无用户交互门——全部用户决策已在 Plan 模式审批卡完成，
/// 本层不再澄清、不再确认（历史上的流内澄清/审批门已随 PlanWorkflow 上线移除）。
/// </summary>
internal sealed class BuildPreambleRunner(BuildRunGate buildRunGate, ToolAssembler toolAssembler)
{
    private readonly BuildRunGate _buildRunGate = buildRunGate;
    private readonly ToolAssembler _toolAssembler = toolAssembler;

    /// <summary>
    /// Build 门禁前置：恢复 durable BuildRun 并自动放行已批准计划。状态事件随执行
    /// 流出；结果经 <paramref name="state"/> 带出（迭代器不能带返回值）。
    /// </summary>
    public async IAsyncEnumerable<QueryEvent> EnsureBuildRunPreambleAsync(
        QueryStreamRequest request,
        BuildPreambleState state,
        [EnumeratorCancellation] CancellationToken ct)
    {
        // Direct Build conversations intentionally stay on the lightweight agent path.
        // MainAgentRunner already owns an EditTransaction and final verification for actual writes.
        // A durable BuildRun is reserved for explicit workflow execution/recovery. The caller
        // sets controlledExecution instead of relying on fragile natural-language intent keywords.
        if (!request.ControlledExecution
            || request.WorkingMode != WorkingMode.Build
            || request.ConversationId is not { } buildConversationId
            || _buildRunGate.Coordinator is not { } coordinator)
        {
            yield break;
        }

        // 受控执行恒由批准计划驱动（PlanWorkflow 审批卡）——预置计划是不变量。
        if (request.PrescribedBuildPlan is null)
        {
            throw new InvalidOperationException(
                "Controlled Build execution requires a prescribed plan approved via the plan workflow.");
        }

        var (buildRun, events) = await ResumeAsync(coordinator, buildConversationId, request.UserPrompt, request, ct)
            .ConfigureAwait(false);
        state.BuildRun = buildRun;
        foreach (var gateEvent in events)
            yield return gateEvent;

        if (state.BuildRun is not { } finalRun)
            yield break;

        if (finalRun.State == BuildRunState.Planned)
        {
            // 上层批准的计划已经过用户决策——BuildRun 的 Planned 门只负责持久化
            // 工具策略，自动放行；工具池为空时仍 fail-closed 拒绝执行。
            var approvedTools = _toolAssembler.SnapshotApprovedTools();
            finalRun = approvedTools.Count == 0
                ? await coordinator.RejectPlanAsync(
                    finalRun.Id,
                    "当前没有可批准的工具策略（工具列表为空）",
                    ct).ConfigureAwait(false)
                : await coordinator.ApprovePlanAsync(
                    finalRun.Id,
                    new ApprovedToolPolicy(approvedTools),
                    "plan-mode-approval",
                    ct).ConfigureAwait(false);
            state.BuildRun = finalRun;
            yield return BuildRunStateEvent.From(finalRun);
        }

        if (BuildStateTransitionService.IsTerminal(finalRun.State))
        {
            if (finalRun.State == BuildRunState.Completed)
                yield return new BuildRunCompletedEvent(BuildRunGate.CreateBuildRunResult(finalRun, finalRun.FailureSummary));
            yield return new DoneEvent(
                null,
                null,
                0,
                BuildRunGate.ResolveTerminalReason(finalRun),
                request.ConversationId);
            state.EarlyDone = true;
            yield break;
        }
    }

    /// <summary>
    /// Begins/resumes the durable BuildRun and buffers the resulting state projections for
    /// one round (1-2 events, unobservable within a single interaction round).
    /// </summary>
    private static async Task<(BuildRun Run, IReadOnlyList<QueryEvent> Events)> ResumeAsync(
        IBuildRunCoordinator coordinator,
        SessionId buildConversationId,
        string prompt,
        QueryStreamRequest request,
        CancellationToken ct)
    {
        List<BuildRun> durableStates = [];
        var run = await coordinator.BeginOrResumeAsync(
            buildConversationId,
            prompt,
            request.WorkingDirectory ?? Environment.CurrentDirectory,
            ct,
            durableStates.Add,
            request.PrescribedBuildPlan).ConfigureAwait(false);

        var events = durableStates.Select(BuildRunStateEvent.From).ToList();
        if (durableStates.Count == 0 || durableStates[^1].Version != run.Version)
            events.Add(BuildRunStateEvent.From(run));
        return (run, events);
    }
}
