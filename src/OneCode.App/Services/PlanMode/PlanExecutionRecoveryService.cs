using Microsoft.Extensions.Hosting;
using OneCode.Core.Build;
using OneCode.Core.PlanMode;

namespace OneCode.App.Services.PlanMode;

/// <summary>
/// Scans persisted execution workflows. FinalizingPlanRun workflows orphaned by a previous process are
/// closed out so the approval prompt becomes available; StartingExecution workflows retry due starts;
/// Executing workflows are reconciled with their durable BuildRun before
/// the idempotent dispatcher restarts execution. The interactive session is attached
/// when the TUI starts; until then the service only preserves persisted recovery state.
/// </summary>
public sealed class PlanExecutionRecoveryService(
    IPlanAggregateStore store,
    IPlanWorkflowApplicationService workflowService,
    IBuildRunStore buildRunStore,
    IPlanAgentRunDispatcher dispatcher,
    PlanRunCloser planRunCloser,
    ILogger<PlanExecutionRecoveryService> logger)
    : BackgroundService
{
    // 孤儿判据：FinalizingPlanRun 在本进程内是活跃 run 的正常中间态（SubmitPlan 到 run 收口之间），
    // 只有进程启动前就停在该状态的计划，其 run 才必然已随旧进程消亡。以进程启动时刻为界，
    // 无需任何跨组件的「run 是否存活」登记。
    private readonly DateTimeOffset _processStartedAt = DateTimeOffset.UtcNow;

    private static readonly TimeSpan ScanInterval = TimeSpan.FromSeconds(5);
    private readonly SemaphoreSlim _scanGate = new(1, 1);
    private readonly HashSet<PlanWorkflowId> _skippedNotLoaded = [];
    private InteractiveSession? _session;

    public void AttachSession(InteractiveSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        Volatile.Write(ref _session, session);
        _ = TryScanDueAsync(CancellationToken.None);
    }

    internal Task ScanDueAsync(CancellationToken ct)
    {
        var session = Volatile.Read(ref _session);
        return session is null
            ? Task.CompletedTask
            : ScanDueAsync(session, DateTimeOffset.UtcNow, ct);
    }

    internal async Task ScanDueAsync(
        InteractiveSession session,
        DateTimeOffset now,
        CancellationToken ct)
    {
        if (!await _scanGate.WaitAsync(0, ct).ConfigureAwait(false))
            return;

        try
        {
            var workflows = await store.LoadRecoverableExecutionAsync(ct).ConfigureAwait(false);

            // 会话未加载期间每 5 秒扫描一次都会命中同一批计划；先清掉已从可恢复列表
            // 消失的计划，避免去重集合无限增长（计划 ID 是 Guid，不会复用）。
            var workflowIds = workflows.Select(w => w.Id).ToHashSet();
            _skippedNotLoaded.RemoveWhere(id => !workflowIds.Contains(id));

            foreach (var workflow in workflows)
            {
                ct.ThrowIfCancellationRequested();
                if (workflow.State == PlanWorkflowState.FinalizingPlanRun
                    && workflow.UpdatedAt >= _processStartedAt)
                {
                    continue;
                }
                if (workflow.State == PlanWorkflowState.StartingExecution
                    && workflow.NextRetryAt is { } retryAt
                    && retryAt > now)
                {
                    continue;
                }
                if (session.SessionManager.GetConversation(workflow.SessionId) is null)
                {
                    // 同一计划只在首次跳过时打日志，状态变化后再打一次（实证：单次会话期间该日志重复 201 条）。
                    if (_skippedNotLoaded.Add(workflow.Id))
                    {
                        logger.LogDebug(
                            "Skipping Plan {PlanId} execution recovery because session {SessionId} is not loaded",
                            workflow.Id,
                            workflow.SessionId);
                    }
                    continue;
                }
                if (_skippedNotLoaded.Remove(workflow.Id))
                {
                    logger.LogDebug(
                        "Plan {PlanId} execution recovery resumed after session {SessionId} loaded",
                        workflow.Id,
                        workflow.SessionId);
                }

                try
                {
                    if (workflow.State == PlanWorkflowState.FinalizingPlanRun)
                    {
                        // 计划已完整提交，只是旧进程没来得及收口：按「run 正常结束」补做收口，
                        // 经同一漏斗推进到待审批（含转录协议校验与审批卡发布）。
                        // TranscriptPersistence.AppendCompletedToolBatchesAsync 只持久化已封口（call+result 成对）的批次，
                        // 未封口的半截调用不会落盘，因此恢复时磁盘转录恒满足 hasOpenToolBatch: false。
                        await planRunCloser.FinishAsync(
                            workflow.SessionId,
                            workflow.ActiveRunId ?? string.Empty,
                            hasOpenToolBatch: false,
                            ct).ConfigureAwait(false);
                    }
                    else if (workflow.State == PlanWorkflowState.StartingExecution)
                    {
                        await dispatcher.StartBuildAsync(session, workflow, ct).ConfigureAwait(false);
                    }
                    else
                    {
                        await RecoverActiveExecutionAsync(session, workflow, ct).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(
                        ex,
                        "Plan {PlanId} automatic execution recovery failed for session {SessionId} in state {State}",
                        workflow.Id,
                        workflow.SessionId,
                        workflow.State);
                }
            }
        }
        finally
        {
            _scanGate.Release();
        }
    }

    private async Task RecoverActiveExecutionAsync(
        InteractiveSession session,
        PlanWorkflow workflow,
        CancellationToken ct)
    {
        ValidateRecoveryIdentity(workflow);
        // 执行世代入口 claim（幂等）：恢复对账与绑定/终态写携带世代令牌，并刷新版本基线。
        workflow = await workflowService.ClaimExecutionAsync(
            workflow.SessionId,
            workflow.Id,
            workflow.Version,
            ct).ConfigureAwait(false);
        var buildRun = string.IsNullOrWhiteSpace(workflow.BuildRunId)
            ? await buildRunStore.LoadAsync(workflow.SessionId, ct).ConfigureAwait(false)
            : await buildRunStore.LoadByIdAsync(
                new BuildRunId(workflow.BuildRunId),
                ct).ConfigureAwait(false);
        if (buildRun is null)
        {
            if (!string.IsNullOrWhiteSpace(workflow.BuildRunId))
            {
                await FailWorkflowAsync(
                    workflow,
                    "BuildRunMissing",
                    $"The Plan workflow references missing BuildRun '{workflow.BuildRunId}'.",
                    ct).ConfigureAwait(false);
                return;
            }

            // The process may have stopped after BuildRunStarted was persisted but before
            // the conversation runner created the first BuildRun checkpoint. Re-entering the existing
            // dispatcher is safe: the prescribed approved snapshot creates the missing run.
            await dispatcher.ResumeBuildAsync(session, workflow, ct).ConfigureAwait(false);
            return;
        }
        if (buildRun.ConversationId != workflow.SessionId)
        {
            await FailWorkflowAsync(
                workflow,
                "BuildRunIdentityMismatch",
                $"Persisted BuildRun '{buildRun.Id}' does not belong to Plan session '{workflow.SessionId}'.",
                ct).ConfigureAwait(false);
            return;
        }
        if (!MatchesApprovedPlan(workflow, buildRun))
        {
            await FailWorkflowAsync(
                workflow,
                "BuildRunPlanMismatch",
                $"Persisted BuildRun '{buildRun.Id}' does not match approved Plan '{workflow.Id}'.",
                ct).ConfigureAwait(false);
            return;
        }
        if (string.IsNullOrWhiteSpace(workflow.BuildRunId))
        {
            workflow = (await workflowService.BindBuildRunAsync(new BindPlanBuildRunCommand(
                $"recover-bind-build-run-{workflow.Id}-{buildRun.Id}",
                workflow.SessionId,
                workflow.Id,
                workflow.ActiveRunId!,
                buildRun.Id.ToString(),
                workflow.WorkflowFencingToken ?? 0), ct).ConfigureAwait(false)).Workflow;
        }

        switch (buildRun.State)
        {
            case BuildRunState.Completed:
                // 进程在 BuildRun 收口之后、工作流收口之前退出：BuildRun 已 Completed 即计划完成。
                await workflowService.HandleRunEventAsync(new BuildRunSucceededEvent(
                    workflow.SessionId,
                    workflow.Id,
                    workflow.ActiveRunId!,
                    DateTimeOffset.UtcNow,
                    workflow.WorkflowFencingToken ?? 0), ct).ConfigureAwait(false);
                return;
            case BuildRunState.Cancelled:
                await workflowService.CancelAsync(new CancelPlanCommand(
                    $"recover-cancel-{workflow.Id}-{workflow.Version}",
                    workflow.SessionId,
                    workflow.Id,
                    workflow.Version,
                    buildRun.FailureSummary ?? "The persisted BuildRun was cancelled."), ct).ConfigureAwait(false);
                return;
            case BuildRunState.Blocked:
            case BuildRunState.Failed:
            case BuildRunState.LimitReached:
            case BuildRunState.BudgetExceeded:
                // Blocked 的产物已保留待人工复核：终态仍是失败，但错误码与文案会说明"文件还在"。
                await FailWorkflowAsync(
                    workflow,
                    BuildRunClosure.FailureCode(buildRun.State),
                    BuildRunClosure.FailureMessage(buildRun.State, buildRun.FailureSummary),
                    ct).ConfigureAwait(false);
                return;
            default:
                await dispatcher.ResumeBuildAsync(session, workflow, ct).ConfigureAwait(false);
                return;
        }
    }

    private async Task FailWorkflowAsync(
        PlanWorkflow workflow,
        string errorCode,
        string errorMessage,
        CancellationToken ct)
    {
        await workflowService.FailExecutionRecoveryAsync(new FailPlanExecutionRecoveryCommand(
            $"recover-fail-{workflow.Id}-{workflow.Version}-{errorCode}",
            workflow.SessionId,
            workflow.Id,
            workflow.Version,
            errorCode,
            errorMessage,
            DateTimeOffset.UtcNow), ct).ConfigureAwait(false);
    }

    private static void ValidateRecoveryIdentity(PlanWorkflow workflow)
    {
        if (string.IsNullOrWhiteSpace(workflow.ExecutionRequestId)
            || string.IsNullOrWhiteSpace(workflow.ActiveRunId))
        {
            throw new PlanTransitionException(
                $"Plan '{workflow.Id}' cannot recover without an execution request and active run identity.");
        }

        var expectedRunId = $"build-{workflow.ExecutionRequestId}";
        if (!string.Equals(workflow.ActiveRunId, expectedRunId, StringComparison.Ordinal))
        {
            throw new PlanTransitionException(
                $"Plan '{workflow.Id}' active run '{workflow.ActiveRunId}' does not match '{expectedRunId}'.");
        }
    }

    private static bool MatchesApprovedPlan(PlanWorkflow workflow, BuildRun buildRun)
    {
        var snapshot = workflow.ApprovedSnapshot;
        var plan = buildRun.Plan;
        if (snapshot is null || plan is null)
            return false;
        if (snapshot.Steps.Count != plan.Tasks.Count)
            return false;

        var tasks = plan.Tasks.ToDictionary(task => task.Id, StringComparer.Ordinal);
        return snapshot.Steps.All(step =>
            tasks.TryGetValue(step.Id, out var task)
            && string.Equals(step.Title, task.Title, StringComparison.Ordinal)
            && string.Equals(step.Description, task.Description, StringComparison.Ordinal)
            && step.DependsOn.SequenceEqual(task.DependsOn, StringComparer.Ordinal)
            && step.Files.SequenceEqual(task.ExpectedFiles, StringComparer.Ordinal)
            && step.AcceptanceCriteria.SequenceEqual(task.AcceptanceCriteria, StringComparer.Ordinal));
    }

    private async Task TryScanDueAsync(CancellationToken ct)
    {
        try
        {
            await ScanDueAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Host shutdown or caller cancellation.
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Plan execution recovery scan failed; the next periodic scan will retry");
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(ScanInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            await TryScanDueAsync(stoppingToken).ConfigureAwait(false);
    }
}
