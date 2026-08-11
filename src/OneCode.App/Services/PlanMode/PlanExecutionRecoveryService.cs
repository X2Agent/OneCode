using Microsoft.Extensions.Hosting;
using OneCode.Core.Build;
using OneCode.Core.PlanMode;

namespace OneCode.App.Services.PlanMode;

/// <summary>
/// Scans persisted execution workflows. StartingExecution workflows retry due starts;
/// Executing and Verifying workflows are reconciled with their durable BuildRun before
/// the idempotent dispatcher restarts execution. The interactive session is attached
/// when the TUI starts; until then the service only preserves persisted recovery state.
/// </summary>
public sealed class PlanExecutionRecoveryService(
    IPlanAggregateStore store,
    IPlanWorkflowApplicationService workflowService,
    IBuildRunStore buildRunStore,
    IPlanAgentRunDispatcher dispatcher,
    ILogger<PlanExecutionRecoveryService> logger)
    : BackgroundService
{
    private static readonly TimeSpan ScanInterval = TimeSpan.FromSeconds(5);
    private readonly SemaphoreSlim _scanGate = new(1, 1);
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
            foreach (var workflow in workflows)
            {
                ct.ThrowIfCancellationRequested();
                if (workflow.State == PlanWorkflowState.StartingExecution
                    && workflow.NextRetryAt is { } retryAt
                    && retryAt > now)
                {
                    continue;
                }
                if (session.SessionManager.GetConversation(workflow.SessionId) is null)
                {
                    logger.LogDebug(
                        "Skipping Plan {PlanId} execution recovery because session {SessionId} is not loaded",
                        workflow.Id,
                        workflow.SessionId);
                    continue;
                }

                try
                {
                    if (workflow.State == PlanWorkflowState.StartingExecution)
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
            // ChatService created the first BuildRun checkpoint. Re-entering the existing
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
                buildRun.Id.ToString()), ct).ConfigureAwait(false)).Workflow;
        }

        switch (buildRun.State)
        {
            case BuildRunState.Completed:
                await FailWorkflowAsync(
                    workflow,
                    "PlanVerificationProtocolMissing",
                    "BuildRun completed, but the Plan workflow did not persist CompletePlanVerification evidence.",
                    ct).ConfigureAwait(false);
                return;
            case BuildRunState.Cancelled:
                await workflowService.CancelAsync(new CancelPlanCommand(
                    $"recover-cancel-{workflow.Id}-{workflow.Version}",
                    workflow.SessionId,
                    workflow.Id,
                    workflow.Version,
                    buildRun.FailureSummary ?? "The persisted BuildRun was cancelled."), ct).ConfigureAwait(false);
                return;
            case BuildRunState.Failed:
            case BuildRunState.Blocked:
            case BuildRunState.LimitReached:
            case BuildRunState.BudgetExceeded:
                await FailWorkflowAsync(
                    workflow,
                    $"BuildRun{buildRun.State}",
                    buildRun.FailureSummary ?? $"The persisted BuildRun terminated in state '{buildRun.State}'.",
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
        if (snapshot is null || plan is null || !plan.RequireExplicitTaskCompletion)
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
