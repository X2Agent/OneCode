using OneCode.App.Services.BuildMode;
using OneCode.Core.Build;
using System.Runtime.CompilerServices;

namespace OneCode.App.Query;

/// <summary>
/// Build 门禁前置（澄清 → 计划审批 → 终态判定）的迭代器实现，拆自
/// <see cref="QueryStreamEngine"/> 为真正的协作类（原为 partial 分片）。无新增共享可变状态，
/// 结果经 <see cref="BuildPreambleState"/> 带出。
/// </summary>
internal sealed class BuildPreambleRunner
{
    private readonly BuildRunGate _buildRunGate;
    private readonly ToolAssembler _toolAssembler;

    public BuildPreambleRunner(BuildRunGate buildRunGate, ToolAssembler toolAssembler)
    {
        _buildRunGate = buildRunGate;
        _toolAssembler = toolAssembler;
    }

    /// <summary>
    /// Build 门禁前置：澄清 → 计划审批 → 终态判定。状态事件随交互逐步流出（时序与
    /// 重构前的内联实现逐语句等价）；结果经 <paramref name="state"/> 带出（迭代器不能带返回值）。
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

        var (buildRun, events) = await ResumeAsync(coordinator, buildConversationId, request.UserPrompt, request, ct)
            .ConfigureAwait(false);
        state.BuildRun = buildRun;
        foreach (var gateEvent in events)
            yield return gateEvent;

        await foreach (var e in RunClarificationLoopAsync(request, coordinator, buildConversationId, state, ct).ConfigureAwait(false))
            yield return e;
        await foreach (var e in RunPlanApprovalLoopAsync(request, coordinator, buildConversationId, state, ct).ConfigureAwait(false))
            yield return e;

        if (state.BuildRun is not { } finalRun)
            yield break;

        if (finalRun.State == BuildRunState.Planned)
        {
            // No interaction channel available — fail closed instead of silently executing.
            var rejected = await coordinator.RejectPlanAsync(
                finalRun.Id,
                "无审批通道（_clarificationInteraction 不可用）",
                ct).ConfigureAwait(false);
            state.BuildRun = rejected;
            yield return BuildRunStateEvent.From(rejected);
            finalRun = rejected;
        }

        if (finalRun.State == BuildRunState.Clarifying
            || BuildStateTransitionService.IsTerminal(finalRun.State))
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

    private async IAsyncEnumerable<QueryEvent> RunClarificationLoopAsync(
        QueryStreamRequest request,
        IBuildRunCoordinator coordinator,
        SessionId buildConversationId,
        BuildPreambleState state,
        [EnumeratorCancellation] CancellationToken ct)
    {
        while (state.BuildRun is { } buildRun
            && buildRun.State == BuildRunState.Clarifying
            && _buildRunGate.Clarification is { } clarificationInteraction)
        {
            var clarification = await clarificationInteraction.AskAsync(
                "开始执行前需要确认",
                buildRun.ClarificationQuestions,
                confirmationOnly: buildRun.ProposedScope is not null,
                ct).ConfigureAwait(false);
            if (clarification.IsCancelled || string.IsNullOrWhiteSpace(clarification.Response))
                break;

            var (resumed, events) = await ResumeAsync(coordinator, buildConversationId, clarification.Response, request, ct)
                .ConfigureAwait(false);
            state.BuildRun = resumed;
            foreach (var gateEvent in events)
                yield return gateEvent;
        }
    }

    /// <summary>
    /// Plan approval gate: the generated plan + tool policy is parked in Planned until the
    /// user approves it. This is a business-layer interaction (same dialog as clarification),
    /// deliberately not a MAF RequestPort — the BuildRun aggregate already persists the
    /// Planned state, so a crash simply re-asks on resume.
    /// </summary>
    private async IAsyncEnumerable<QueryEvent> RunPlanApprovalLoopAsync(
        QueryStreamRequest request,
        IBuildRunCoordinator coordinator,
        SessionId buildConversationId,
        BuildPreambleState state,
        [EnumeratorCancellation] CancellationToken ct)
    {
        while (state.BuildRun is { } buildRun
            && buildRun.State == BuildRunState.Planned
            && _buildRunGate.Clarification is { } clarificationInteraction)
        {
            // 计划审批门（流内门）：决策非持久化——BuildRun 聚合的 Planned 态
            // 已落盘，崩溃后 resume 重问。审批机制按模式自持、不强行统一，
            // 流内确认语义：取消/空白回复一律视为拒绝。
            // 提问文本只展示计划摘要；工具策略由 SnapshotApprovedTools 快照直接走
            // ApprovePlanAsync 持久化，对用户决策无增量信息，不在卡片展示。
            var approvedTools = _toolAssembler.SnapshotApprovedTools();
            var answer = await clarificationInteraction.AskAsync(
                "计划已生成，请确认后开始执行",
                // 不变量：进入 Planned 前已由 BuildPlanValidator 保证 Plan 与 Summary 非空。
                [buildRun.Plan!.Summary],
                confirmationOnly: true,
                ct).ConfigureAwait(false);
            var approved = !answer.IsCancelled && !string.IsNullOrWhiteSpace(answer.Response);
            buildRun = !approved
                ? await coordinator.RejectPlanAsync(
                    buildRun.Id,
                    "用户取消计划审批",
                    ct).ConfigureAwait(false)
                : approvedTools.Count == 0
                    ? await coordinator.RejectPlanAsync(
                        buildRun.Id,
                        "当前没有可批准的工具策略（工具列表为空）",
                        ct).ConfigureAwait(false)
                    : await coordinator.ApprovePlanAsync(
                        buildRun.Id,
                        new ApprovedToolPolicy(approvedTools),
                        "runtime-approved",
                        ct).ConfigureAwait(false);
            state.BuildRun = buildRun;
            yield return BuildRunStateEvent.From(buildRun);
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
        var durableStates = new List<BuildRun>();
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
