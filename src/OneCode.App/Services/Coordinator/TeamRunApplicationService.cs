using OneCode.Core.Build;
using OneCode.Core.Coordinator;
using OneCode.Core.Errors;
using OneCode.Infrastructure.Agent;

namespace OneCode.App.Services.Coordinator;

public sealed class TeamRunApplicationService(
    ITeamRunStore store,
    TeamRunStateMachine stateMachine,
    TeamQualityGateRunner qualityGateRunner,
    DeliveryReportBuilder deliveryReportBuilder,
    IWorkspaceFingerprintProvider? fingerprintProvider = null)
{
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
        ValidatePlan(plan);
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
        ValidatePlan(plan);
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
        ValidatePlan(plan);
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

            if (!PlansMatch(existing.Plan, plan))
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

    private static bool PlansMatch(ImplementationPlan left, ImplementationPlan right)
        => JsonSerializer.Serialize(left) == JsonSerializer.Serialize(right);

    public async Task<TeamRun> StartTaskAsync(
        TeamRun run,
        string taskId,
        CancellationToken ct)
        => await StartTaskAsync(run, taskId, null, ct).ConfigureAwait(false);

    /// <summary>
    /// 按 ID 重载并启动任务（DAG 并行执行器入口）。
    /// 必须携带当前 Workflow FencingToken；与磁盘不一致时 fail-closed。
    /// </summary>
    public async Task<TeamRun> StartTaskAsync(
        TeamRunId runId,
        string taskId,
        long fencingToken,
        CancellationToken ct)
    {
        var run = await RequireRunAsync(runId, fencingToken, ct).ConfigureAwait(false);
        return await StartTaskAsync(run, taskId, fencingToken, ct).ConfigureAwait(false);
    }

    public async Task<TeamRun> StartTaskAsync(
        TeamRun run,
        string taskId,
        long? fencingToken,
        CancellationToken ct)
    {
        RequireFence(run, fencingToken);
        // Task ordering and write-conflict guards are enforced by MAF DAG topology
        // (fan-out/fan-in/barrier edges) in TeamTaskWorkflowCompiler; this method only
        // records the business fact that a new attempt has begun. Status remains null
        // until CompleteTaskAsync sets the terminal outcome.
        var tasks = run.TaskGraph!.Tasks
            .Select(task => task.Definition.Id == taskId
                ? task with { Attempt = task.Attempt + 1 }
                : task)
            .ToList();
        var updated = run with
        {
            TaskGraph = new TeamTaskGraph(tasks),
            Version = checked(run.Version + 1),
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        await SaveOrThrowAsync(updated, run.Version, ct).ConfigureAwait(false);
        return updated;
    }

    public async Task<TeamRun> CompleteTaskAsync(
        TeamRun run,
        string taskId,
        TeamRunResult execution,
        CancellationToken ct)
        => await CompleteTaskAsync(run, taskId, execution, null, ct).ConfigureAwait(false);

    /// <summary>按 ID 重载并完成任务（DAG 并行执行器入口），必须携带当前 FencingToken。</summary>
    public async Task<TeamRun> CompleteTaskAsync(
        TeamRunId runId,
        string taskId,
        TeamRunResult execution,
        long fencingToken,
        CancellationToken ct)
    {
        var run = await RequireRunAsync(runId, fencingToken, ct).ConfigureAwait(false);
        return await CompleteTaskAsync(run, taskId, execution, fencingToken, ct).ConfigureAwait(false);
    }

    public async Task<TeamRun> CompleteTaskAsync(
        TeamRun run,
        string taskId,
        TeamRunResult execution,
        long? fencingToken,
        CancellationToken ct)
    {
        RequireFence(run, fencingToken);
        var currentTask = run.TaskGraph!.Tasks.SingleOrDefault(task => task.Definition.Id == taskId)
            ?? throw new InvalidOperationException($"Team task '{taskId}' was not found.");
        if (currentTask.Status is not null)
            throw new InvalidOperationException($"Team task '{taskId}' already reached terminal status {currentTask.Status}.");

        var failure = ResolveExecutionFailure(execution);
        var taskStatus = failure is not null
            ? TeamTaskStatus.Failed
            : TeamTaskStatus.Succeeded;
        var errorFingerprint = failure is not null
            ? ComputeErrorFingerprint(failure.Detail ?? failure.Title)
            : null;
        var tasks = run.TaskGraph!.Tasks
            .Select(task => task.Definition.Id == taskId
                ? task with
                {
                    Status = taskStatus,
                    Summary = execution.Output,
                    Failure = failure,
                    ErrorFingerprint = errorFingerprint,
                }
                : task)
            .ToList();
        // C2: Succeeded 任务落库时记录工作区指纹，恢复世代用它与当前指纹比对，
        // 检测已完成任务的文件改动是否已被回滚/篡改。工作区不可读时保守跳过（保持原值）。
        var lastTaskFingerprint = run.LastTaskFingerprint;
        if (taskStatus == TeamTaskStatus.Succeeded && fingerprintProvider is not null)
        {
            try
            {
                lastTaskFingerprint = await fingerprintProvider.ComputeAsync(run.WorkingDirectory, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or DirectoryNotFoundException or UnauthorizedAccessException)
            {
            }
        }
        var updated = run with
        {
            TaskGraph = new TeamTaskGraph(tasks),
            Failure = taskStatus == TeamTaskStatus.Failed ? failure : run.Failure,
            LastTaskFingerprint = lastTaskFingerprint,
            Version = checked(run.Version + 1),
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        await SaveOrThrowAsync(updated, run.Version, ct).ConfigureAwait(false);
        return updated;
    }

    /// <summary>
    /// 恢复前的已完成任务对账（C2）：调用方须先执行 ledger reconcile（回滚上一世代未提交的
    /// 文件副作用），再调用本方法比对指纹。不一致说明 Succeeded 任务的改动已不在盘，
    /// 将其降级为待执行（Status=null）以便新世代重跑，防止"聚合记 Succeeded、文件已回滚"的静默丢失。
    /// </summary>
    public async Task<TeamRun> ReconcileSucceededTasksAsync(TeamRun run, CancellationToken ct = default)
    {
        if (fingerprintProvider is null
            || run.TaskGraph is null
            || run.LastTaskFingerprint is not { } expectedFingerprint)
        {
            return run;
        }
        if (!run.TaskGraph.Tasks.Any(task => task.Status == TeamTaskStatus.Succeeded))
            return run;

        string currentFingerprint;
        try
        {
            currentFingerprint = await fingerprintProvider.ComputeAsync(run.WorkingDirectory, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or DirectoryNotFoundException or UnauthorizedAccessException)
        {
            // 工作区不可读：保守跳过校验，维持既有恢复语义。
            return run;
        }

        if (string.Equals(currentFingerprint, expectedFingerprint, StringComparison.Ordinal))
            return run;

        var tasks = run.TaskGraph.Tasks
            .Select(task => task.Status == TeamTaskStatus.Succeeded
                ? task with { Status = null }
                : task)
            .ToList();
        var updated = run with
        {
            TaskGraph = new TeamTaskGraph(tasks),
            LastTaskFingerprint = null,
            Version = checked(run.Version + 1),
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        await SaveOrThrowAsync(updated, run.Version, ct).ConfigureAwait(false);
        return updated;
    }

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

    public async Task<TeamRun> CompleteExecutionAsync(
        TeamRun run,
        TeamRunResult execution,
        EditTransaction transaction,
        IReadOnlyList<FileChange> fileChanges,
        CancellationToken ct)
        => await CompleteExecutionAsync(run, execution, transaction, fileChanges, null, ct).ConfigureAwait(false);

    /// <summary>按 ID 重载并完成整个执行（DAG 完成阶段入口），必须携带当前 FencingToken。</summary>
    public async Task<TeamRun> CompleteExecutionAsync(
        TeamRunId runId,
        TeamRunResult execution,
        EditTransaction transaction,
        IReadOnlyList<FileChange> fileChanges,
        long fencingToken,
        CancellationToken ct)
    {
        var run = await RequireRunAsync(runId, fencingToken, ct).ConfigureAwait(false);
        return await CompleteExecutionAsync(run, execution, transaction, fileChanges, fencingToken, ct).ConfigureAwait(false);
    }

    public async Task<TeamRun> CompleteExecutionAsync(
        TeamRun run,
        TeamRunResult execution,
        EditTransaction transaction,
        IReadOnlyList<FileChange> fileChanges,
        long? fencingToken,
        CancellationToken ct,
        OneCode.Core.Workflows.IOperationLedger? operationLedger = null,
        string? operationId = null)
    {
        RequireFence(run, fencingToken);
        var taskStatus = ResolveExecutionFailure(execution) is not null
            || run.TaskGraph?.RequiredTasks.Any(task => task.Status != TeamTaskStatus.Succeeded) != false
            ? TeamTaskStatus.Failed
            : TeamTaskStatus.Succeeded;
        var updated = run with
        {
            Changes = new ChangeSetSummary(
                fileChanges,
                fileChanges.Sum(f => f.AddedLines.Count),
                fileChanges.Sum(f => f.RemovedLines.Count)),
            Failure = ResolveExecutionFailure(execution),
        };

        if (taskStatus != TeamTaskStatus.Succeeded)
        {
            updated = updated with
            {
                Failure = updated.Failure ?? AgentProblemDetails.ToolExecutionFailed(
                    "One or more required Team tasks did not succeed.",
                    toolName: "TeamTaskExecution"),
            };
            return await RollBackAsync(
                updated,
                transaction,
                run.Version,
                "Team execution failed; file changes were rolled back.",
                ct).ConfigureAwait(false);
        }

        updated = stateMachine.Transition(
            updated,
            TeamRunPhase.Verification,
            TeamRunStatus.Running,
            DateTimeOffset.UtcNow);
        await SaveOrThrowAsync(updated, run.Version, ct).ConfigureAwait(false);

        var gateResults = await qualityGateRunner.RunAsync(
            updated.Plan!.RequiredGates,
            updated.WorkingDirectory,
            transaction,
            updated,
            ct).ConfigureAwait(false);
        var requiredGatesPassed = gateResults.Where(g => g.Required)
            .All(g => g.Status == QualityGateStatus.Passed);
        updated = updated with { GateResults = gateResults };

        if (!requiredGatesPassed)
        {
            return await RollBackAsync(
                updated,
                transaction,
                updated.Version,
                "Required Team quality gates failed; file changes were rolled back.",
                ct).ConfigureAwait(false);
        }

        updated = stateMachine.Transition(
            updated,
            TeamRunPhase.Delivery,
            TeamRunStatus.Running,
            DateTimeOffset.UtcNow);
        await SaveOrThrowAsync(updated, updated.Version - 1, ct).ConfigureAwait(false);

        if (!stateMachine.CanCommit(updated))
        {
            return await RollBackAsync(
                updated,
                transaction,
                updated.Version,
                "TeamRun CanCommit invariant rejected delivery.",
                ct).ConfigureAwait(false);
        }

        var delivery = deliveryReportBuilder.Build(updated, committed: true, execution.Output);
        updated = updated with
        {
            Delivery = delivery,
            TransactionCommitted = true,
        };
        updated = stateMachine.Transition(
            updated,
            TeamRunPhase.Completed,
            TeamRunStatus.Succeeded,
            DateTimeOffset.UtcNow);

        // Persist the deterministic commit decision before releasing transaction snapshots.
        // If persistence fails, the caller's using scope still disposes the uncommitted
        // transaction and restores files. EditTransaction.Commit performs no external I/O.
        await SaveOrThrowAsync(updated, updated.Version - 1, ct).ConfigureAwait(false);

        // S-04: 先持久化提交（ledger receipt）再内存提交——防止"内存已提交、ledger 未提交"崩溃后误回滚。
        if (operationLedger is not null && operationId is not null && fencingToken is { } fence)
        {
            await operationLedger.CommitTransactionAsync(
                operationId,
                fence,
                $"team-execution-committed:{run.Id}",
                ct).ConfigureAwait(false);
        }

        transaction.Commit();
        return updated;
    }

    private async Task<TeamRun> RollBackAsync(
        TeamRun run,
        EditTransaction transaction,
        long expectedVersion,
        string summary,
        CancellationToken ct,
        long? fencingToken = null)
    {
        transaction.Rollback();
        var delivery = run.TaskGraph is null
            ? null
            : deliveryReportBuilder.Build(run, committed: false, summary);
        var rolledBack = run with { Delivery = delivery };
        rolledBack = stateMachine.Transition(
            rolledBack,
            TeamRunPhase.Completed,
            TeamRunStatus.RolledBack,
            DateTimeOffset.UtcNow);
        await SaveOrThrowAsync(rolledBack, expectedVersion, ct).ConfigureAwait(false);
        return rolledBack;
    }

    private static AgentProblemDetails? ResolveExecutionFailure(TeamRunResult execution)
    {
        if (execution.Error is not null)
            return execution.Error;
        if (execution.HadFailures)
        {
            return AgentProblemDetails.ToolExecutionFailed(
                "Team workflow reported one or more agent failures.",
                toolName: "TeamTaskExecution");
        }
        if (execution.MaxTurnsReached)
        {
            return AgentProblemDetails.ToolExecutionFailed(
                "Team workflow reached its maximum turn limit before completing the task.",
                toolName: "TeamTaskExecution");
        }
        if (execution.TurnsCompleted == 0
            || string.IsNullOrWhiteSpace(execution.Output)
            || string.Equals(execution.Output.Trim(), "(no output)", StringComparison.Ordinal))
        {
            return AgentProblemDetails.ToolExecutionFailed(
                "Team workflow completed without any agent response.",
                toolName: "TeamTaskExecution",
                suggestedNextAction: "Check the ChatClient/model configuration and Team workflow logs.");
        }
        return null;
    }

    private static string ComputeErrorFingerprint(string error)
        => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(error))).ToLowerInvariant()[..16];

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

    private static void RequireFence(TeamRun run, long? fencingToken)
    {
        if (fencingToken is not { } token)
            return;
        if (run.WorkflowFencingToken != token)
        {
            throw new InvalidOperationException(
                $"TeamRun '{run.Id}' fencing token mismatch: expected {run.WorkflowFencingToken?.ToString(CultureInfo.InvariantCulture) ?? "(none)"}, attempted {token}.");
        }
    }

    private async Task SaveOrThrowAsync(TeamRun run, long expectedVersion, CancellationToken ct)
    {
        if (!await store.TrySaveAsync(run, expectedVersion, ct).ConfigureAwait(false))
            throw new InvalidOperationException($"TeamRun '{run.Id}' version conflict while saving version {run.Version}.");
    }

    private static void ValidatePlan(ImplementationPlan plan)
    {
        if (plan.Tasks.Count == 0)
            throw new InvalidOperationException("Team implementation plan must contain tasks.");
        if (plan.RequiredGates.All(g => !g.Required))
            throw new InvalidOperationException("Team implementation plan must contain a required quality gate.");
        var ids = plan.Tasks.Select(t => t.Id).ToList();
        if (ids.Count != ids.Distinct(StringComparer.Ordinal).Count())
            throw new InvalidOperationException("Team task IDs must be unique.");
        if (plan.Tasks.SelectMany(t => t.DependsOn).Any(id => !ids.Contains(id, StringComparer.Ordinal)))
            throw new InvalidOperationException("Team task dependency references an unknown task.");
        ValidateAcyclic(plan.Tasks);
        if (plan.Tasks.Where(t => t.ToolPolicy == TeamToolPolicy.WriteAllowed)
            .Any(t => t.AcceptanceCriteria.Count == 0))
        {
            throw new InvalidOperationException("Every Team write task requires acceptance criteria.");
        }
    }

    private static void ValidateAcyclic(IReadOnlyList<TeamTaskDefinition> tasks)
    {
        var dependencies = tasks.ToDictionary(
            task => task.Id,
            task => task.DependsOn,
            StringComparer.Ordinal);
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);

        foreach (var task in tasks)
        {
            if (HasCycle(task.Id))
                throw new InvalidOperationException("Team task graph contains a dependency cycle.");
        }

        bool HasCycle(string taskId)
        {
            if (visited.Contains(taskId))
                return false;
            if (!visiting.Add(taskId))
                return true;
            foreach (var dependency in dependencies[taskId])
            {
                if (HasCycle(dependency))
                    return true;
            }
            visiting.Remove(taskId);
            visited.Add(taskId);
            return false;
        }
    }
}
