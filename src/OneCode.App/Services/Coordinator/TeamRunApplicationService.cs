using OneCode.App.Services.Runtime;
using OneCode.Core.Build;
using OneCode.Core.Coordinator;
using OneCode.Infrastructure.Agent;

namespace OneCode.App.Services.Coordinator;

/// <summary>
/// TeamRun lifecycle facade (W3-A): clarification/approval/cancel + fencing entry points.
/// Execution finalization lives in <see cref="TeamRunFinalizer"/>; task progress in <see cref="TeamTaskProgress"/>.
/// </summary>
public sealed class TeamRunApplicationService(
    ITeamRunStore store,
    TeamRunStateMachine stateMachine,
    WorkflowQualityGateRunner qualityGateRunner,
    DeliveryReportBuilder deliveryReportBuilder,
    IWorkspaceFingerprintProvider? fingerprintProvider = null)
{
    private readonly TeamRunFinalizer _finalizer = new(
        store, stateMachine, qualityGateRunner, deliveryReportBuilder);
    private readonly TeamTaskProgress _progress = new(store, fingerprintProvider);

    public async Task<TeamRun> BeginClarificationAsync(
        TeamRunId runId,
        string teamName,
        string request,
        string workingDirectory,
        IReadOnlyList<string> questions,
        CancellationToken ct,
        SessionId? sessionId = null)
    {
        var now = DateTimeOffset.UtcNow;
        var run = new TeamRun
        {
            Id = runId,
            TeamName = teamName,
            OriginalRequest = request,
            WorkingDirectory = workingDirectory,
            Phase = TeamRunPhase.Clarification,
            Status = TeamRunStatus.WaitingForUser,
            Requirements = new RequirementBaseline(
                request, [request], [], [], [], [], questions, RequiresApproval: true),
            SessionId = sessionId,
            Version = 1,
            CreatedAt = now,
            UpdatedAt = now,
        };
        if (!await store.TrySaveAsync(run, 0, ct).ConfigureAwait(false))
            throw new InvalidOperationException($"Failed to create clarification TeamRun '{runId}'.");
        return run;
    }

    public async Task<TeamRun> PromoteClarificationToApprovalAsync(
        TeamRunId runId,
        string clarifiedRequest,
        ImplementationPlan plan,
        CancellationToken ct)
    {
        TeamRunGuards.ValidatePlan(plan);
        var current = await store.LoadAsync(runId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"TeamRun '{runId}' was not found.");
        if (current.Phase != TeamRunPhase.Clarification
            || current.Status != TeamRunStatus.WaitingForUser)
            throw new InvalidOperationException($"TeamRun '{runId}' is not awaiting clarification.");
        var updated = current with
        {
            OriginalRequest = clarifiedRequest,
            Phase = TeamRunPhase.AwaitingApproval,
            Status = TeamRunStatus.WaitingForUser,
            Requirements = new RequirementBaseline(
                clarifiedRequest,
                [clarifiedRequest],
                [],
                plan.Tasks.SelectMany(task => task.AcceptanceCriteria).Distinct().ToList(),
                [],
                [],
                [],
                RequiresApproval: true),
            Plan = plan,
            TaskGraph = new TeamTaskGraph(
                plan.Tasks.Select(task => new TeamTaskState(task, Status: null)).ToList()),
            Version = checked(current.Version + 1),
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        await SaveOrThrowAsync(updated, current.Version, ct).ConfigureAwait(false);
        return updated;
    }

    /// <summary>
    /// Persists the business run before the durable approval workflow is started.
    /// This ordering prevents a pending approval checkpoint from becoming an orphan
    /// that the business recovery registry cannot discover.
    /// </summary>
    public async Task<TeamRun> BeginApprovalAsync(
        TeamRunId runId,
        string teamName,
        string request,
        string workingDirectory,
        ImplementationPlan plan,
        CancellationToken ct,
        SessionId? sessionId = null)
    {
        TeamRunGuards.ValidatePlan(plan);
        var now = DateTimeOffset.UtcNow;
        var tasks = plan.Tasks
            .Select(task => new TeamTaskState(task, Status: null))
            .ToList();
        var run = new TeamRun
        {
            Id = runId,
            TeamName = teamName,
            OriginalRequest = request,
            WorkingDirectory = workingDirectory,
            Phase = TeamRunPhase.AwaitingApproval,
            Status = TeamRunStatus.WaitingForUser,
            Requirements = new RequirementBaseline(
                request,
                [request],
                [],
                plan.Tasks.SelectMany(task => task.AcceptanceCriteria).Distinct().ToList(),
                [],
                [],
                [],
                RequiresApproval: true),
            Plan = plan,
            TaskGraph = new TeamTaskGraph(tasks),
            SessionId = sessionId,
            Version = 1,
            CreatedAt = now,
            UpdatedAt = now,
        };

        if (!await store.TrySaveAsync(run, expectedVersion: 0, ct).ConfigureAwait(false))
            throw new InvalidOperationException($"Failed to create TeamRun '{run.Id}'.");
        return run;
    }

    public async Task<TeamRun> BeginApprovedExecutionAsync(
        TeamRunId runId,
        string teamName,
        string request,
        string workingDirectory,
        ImplementationPlan plan,
        CancellationToken ct,
        SessionId? sessionId = null)
    {
        TeamRunGuards.ValidatePlan(plan);
        var existing = await store.LoadAsync(runId, ct).ConfigureAwait(false);
        if (existing is not null)
        {
            if (existing.Phase != TeamRunPhase.AwaitingApproval
                || existing.Status != TeamRunStatus.WaitingForUser
                || existing.Plan is null)
            {
                throw new InvalidOperationException(
                    $"TeamRun '{runId}' is not awaiting approval.");
            }

            if (!TeamRunGuards.PlansMatch(existing.Plan, plan))
                throw new InvalidOperationException(
                    $"Approved plan for TeamRun '{runId}' does not match the persisted plan.");

            var approved = stateMachine.Transition(
                existing with { PlanApproved = true },
                TeamRunPhase.Execution,
                TeamRunStatus.Running,
                DateTimeOffset.UtcNow);
            await SaveOrThrowAsync(approved, existing.Version, ct).ConfigureAwait(false);
            return approved;
        }

        var now = DateTimeOffset.UtcNow;
        // Tasks start with no terminal status (null); MAF DAG topology drives execution
        // order via fan-out/fan-in/barrier edges. StartTaskAsync increments Attempt;
        // CompleteTaskAsync sets the terminal status.
        var tasks = plan.Tasks
            .Select(t => new TeamTaskState(t, Status: null))
            .ToList();
        var run = new TeamRun
        {
            Id = runId,
            TeamName = teamName,
            OriginalRequest = request,
            WorkingDirectory = workingDirectory,
            Phase = TeamRunPhase.Execution,
            Status = TeamRunStatus.Running,
            Requirements = new RequirementBaseline(
                request,
                [request],
                [],
                plan.Tasks.SelectMany(t => t.AcceptanceCriteria).Distinct().ToList(),
                [],
                [],
                [],
                RequiresApproval: true),
            Plan = plan,
            TaskGraph = new TeamTaskGraph(tasks),
            PlanApproved = true,
            SessionId = sessionId,
            Version = 1,
            CreatedAt = now,
            UpdatedAt = now,
        };

        if (!await store.TrySaveAsync(run, expectedVersion: 0, ct).ConfigureAwait(false))
            throw new InvalidOperationException($"Failed to create TeamRun '{run.Id}'.");
        return run;
    }

    public Task<TeamRun> StartTaskAsync(TeamRun run, string taskId, CancellationToken ct)
        => _progress.StartTaskAsync(run, taskId, ct);

    /// <summary>
    /// Load by id then start task (DAG execution entry). Caller must present current FencingToken.
    /// </summary>
    public async Task<TeamRun> StartTaskAsync(
        TeamRunId runId,
        string taskId,
        long fencingToken,
        CancellationToken ct)
    {
        var run = await RequireRunAsync(runId, fencingToken, ct).ConfigureAwait(false);
        return await _progress.StartTaskAsync(run, taskId, fencingToken, ct).ConfigureAwait(false);
    }

    public Task<TeamRun> StartTaskAsync(
        TeamRun run,
        string taskId,
        long? fencingToken,
        CancellationToken ct)
        => _progress.StartTaskAsync(run, taskId, fencingToken, ct);

    public Task<TeamRun> CompleteTaskAsync(
        TeamRun run,
        string taskId,
        TeamRunResult execution,
        CancellationToken ct)
        => _progress.CompleteTaskAsync(run, taskId, execution, ct);

    /// <summary>Load by id then complete task; caller must present current FencingToken.</summary>
    public async Task<TeamRun> CompleteTaskAsync(
        TeamRunId runId,
        string taskId,
        TeamRunResult execution,
        long fencingToken,
        CancellationToken ct)
    {
        var run = await RequireRunAsync(runId, fencingToken, ct).ConfigureAwait(false);
        return await _progress.CompleteTaskAsync(run, taskId, execution, fencingToken, ct).ConfigureAwait(false);
    }

    public Task<TeamRun> CompleteTaskAsync(
        TeamRun run,
        string taskId,
        TeamRunResult execution,
        long? fencingToken,
        CancellationToken ct)
        => _progress.CompleteTaskAsync(run, taskId, execution, fencingToken, ct);

    public Task<TeamRun> ReconcileSucceededTasksAsync(TeamRun run, CancellationToken ct = default)
        => _progress.ReconcileSucceededTasksAsync(run, ct);

    public Task<TeamRun> CompleteExecutionAsync(
        TeamRun run,
        TeamRunResult execution,
        EditTransaction transaction,
        IReadOnlyList<FileChange> fileChanges,
        CancellationToken ct)
        => _finalizer.CompleteExecutionAsync(run, execution, transaction, fileChanges, null, ct);

    /// <summary>Load by id then complete execution; caller must present current FencingToken.</summary>
    public async Task<TeamRun> CompleteExecutionAsync(
        TeamRunId runId,
        TeamRunResult execution,
        EditTransaction transaction,
        IReadOnlyList<FileChange> fileChanges,
        long fencingToken,
        CancellationToken ct)
    {
        var run = await RequireRunAsync(runId, fencingToken, ct).ConfigureAwait(false);
        return await _finalizer.CompleteExecutionAsync(
            run, execution, transaction, fileChanges, fencingToken, ct).ConfigureAwait(false);
    }

    public Task<TeamRun> CompleteExecutionAsync(
        TeamRun run,
        TeamRunResult execution,
        EditTransaction transaction,
        IReadOnlyList<FileChange> fileChanges,
        long? fencingToken,
        CancellationToken ct,
        OneCode.Core.Workflows.IOperationLedger? operationLedger = null,
        string? operationId = null)
        => _finalizer.CompleteExecutionAsync(
            run, execution, transaction, fileChanges, fencingToken, ct, operationLedger, operationId);

    /// <summary>
    /// 用户取消（H1）：把运行中的 TeamRun 落为 Cancelled 终态。非终态任务标记 Cancelled；
    /// Succeeded 任务保留为历史事实，交付报告以 committed:false 记录改动未保留
    /// （取消路径的 run 级文件事务已整体回滚）。
    /// </summary>
    public async Task<TeamRun> CancelAsync(TeamRunId runId, string summary, CancellationToken ct = default)
    {
        var current = await store.LoadAsync(runId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"TeamRun '{runId}' was not found.");
        if (TeamRunStateMachine.IsTerminal(current.Status))
            return current;

        var tasks = current.TaskGraph?.Tasks
            .Select(task => task.Status is null
                ? task with { Status = TeamTaskStatus.Cancelled }
                : task)
            .ToList();
        var updated = current with
        {
            TaskGraph = tasks is null ? null : new TeamTaskGraph(tasks),
        };
        if (updated.TaskGraph is not null)
        {
            updated = updated with
            {
                Delivery = deliveryReportBuilder.Build(updated, committed: false, summary),
            };
        }
        updated = stateMachine.Transition(
            updated,
            TeamRunPhase.Completed,
            TeamRunStatus.Cancelled,
            DateTimeOffset.UtcNow);
        await SaveOrThrowAsync(updated, current.Version, ct).ConfigureAwait(false);
        return updated;
    }

    private async Task<TeamRun> RequireRunAsync(
        TeamRunId runId,
        long fencingToken,
        CancellationToken ct)
    {
        if (fencingToken <= 0)
            throw new ArgumentOutOfRangeException(nameof(fencingToken), "Fencing token must be positive.");
        var run = await store.LoadAsync(runId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"TeamRun '{runId}' was not found.");
        if (run.WorkflowFencingToken != fencingToken)
        {
            throw new InvalidOperationException(
                $"TeamRun '{runId}' is held by workflow fencing token {run.WorkflowFencingToken?.ToString(CultureInfo.InvariantCulture) ?? "(none)"}; caller presented {fencingToken}.");
        }
        return run;
    }

    private async Task SaveOrThrowAsync(TeamRun run, long expectedVersion, CancellationToken ct)
    {
        if (!await store.TrySaveAsync(run, expectedVersion, ct).ConfigureAwait(false))
            throw new InvalidOperationException($"TeamRun '{run.Id}' version conflict while saving version {run.Version}.");
    }
}
