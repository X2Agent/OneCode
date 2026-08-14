using OneCode.App.Services.BuildMode;
using OneCode.App.Tui;
using OneCode.Core.Build;
using System.Runtime.CompilerServices;

namespace OneCode.App.Query;

/// <summary>
/// Build 门禁前置（澄清 → 计划审批 → 终态判定）的迭代器实现，拆自
/// <see cref="QueryStreamEngine"/> 以控制单文件规模。按职责分 partial（无新增共享可变状态，
/// 结果经 <see cref="BuildPreambleState"/> 带出），不违反 ADR 0006 的状态对象原则。
/// </summary>
internal sealed partial class QueryStreamEngine
{
    /// <summary>
    /// Build 门禁前置：澄清 → 计划审批 → 终态判定。状态事件随交互逐步流出（时序与
    /// 重构前的内联实现逐语句等价）；结果经 <paramref name="state"/> 带出（迭代器不能带返回值）。
    /// </summary>
    private async IAsyncEnumerable<QueryEvent> EnsureBuildRunPreambleAsync(
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
            var approvedTools = SnapshotApprovedTools();
            var approval = await clarificationInteraction.AskAsync(
                "计划已生成，请确认后开始执行",
                [BuildPlanApprovalPrompt(buildRun, approvedTools)],
                confirmationOnly: true,
                ct).ConfigureAwait(false);
            buildRun = approval.IsCancelled || string.IsNullOrWhiteSpace(approval.Response)
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

/// <summary>
/// Outcome carrier for the Build gate preamble: async iterators cannot return values,
/// so the preamble writes its result here while yielding gate events.
/// </summary>
internal sealed class BuildPreambleState
{
    public BuildRun? BuildRun { get; set; }

    public bool EarlyDone { get; set; }
}
