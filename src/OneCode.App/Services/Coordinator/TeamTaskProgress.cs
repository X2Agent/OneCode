using OneCode.Core.Build;
using OneCode.Core.Coordinator;

namespace OneCode.App.Services.Coordinator;

/// <summary>
/// TeamRun 任务图的进度变更。
/// </summary>
internal sealed class TeamTaskProgress(
    ITeamRunStore store,
    IWorkspaceFingerprintProvider? fingerprintProvider = null)
{
    public Task<TeamRun> StartTaskAsync(TeamRun run, string taskId, CancellationToken ct)
        => StartTaskAsync(run, taskId, null, ct);

    public async Task<TeamRun> StartTaskAsync(
        TeamRun run,
        string taskId,
        long? fencingToken,
        CancellationToken ct)
    {
        TeamRunGuards.RequireFence(run, fencingToken);
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

    public Task<TeamRun> CompleteTaskAsync(
        TeamRun run,
        string taskId,
        TeamRunResult execution,
        CancellationToken ct)
        => CompleteTaskAsync(run, taskId, execution, null, ct);

    public async Task<TeamRun> CompleteTaskAsync(
        TeamRun run,
        string taskId,
        TeamRunResult execution,
        long? fencingToken,
        CancellationToken ct)
    {
        TeamRunGuards.RequireFence(run, fencingToken);
        var currentTask = run.TaskGraph!.Tasks.SingleOrDefault(task => task.Definition.Id == taskId)
            ?? throw new InvalidOperationException($"Team task '{taskId}' was not found.");
        if (currentTask.Status is not null)
            throw new InvalidOperationException($"Team task '{taskId}' already reached terminal status {currentTask.Status}.");

        var failure = TeamRunGuards.ResolveExecutionFailure(execution);
        var taskStatus = failure is not null
            ? TeamTaskStatus.Failed
            : TeamTaskStatus.Succeeded;
        var errorFingerprint = failure is not null
            ? TeamRunGuards.ComputeErrorFingerprint(failure.Detail ?? failure.Title)
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

    private async Task SaveOrThrowAsync(TeamRun run, long expectedVersion, CancellationToken ct)
    {
        if (!await store.TrySaveAsync(run, expectedVersion, ct).ConfigureAwait(false))
            throw new InvalidOperationException($"TeamRun '{run.Id}' version conflict while saving version {run.Version}.");
    }

}
