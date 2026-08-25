using OneCode.Core.Build;
using OneCode.Core.Tasks;
using TaskStatus = OneCode.Core.Tasks.TaskStatus;

namespace OneCode.App.Services.BuildMode;

/// <summary>
/// BuildPlan 任务与持久化 TaskItem 之间的链接器：拓扑序建链、恢复重置、终态标记。
/// 拥有 ITaskService 依赖，是 BuildRun 领域计划与任务系统之间的唯一通道——
/// 协调器与其余 partial 不直接触碰 ITaskService。
/// </summary>
public sealed class BuildTaskLinker(ITaskService taskService)
{
    /// <summary>按依赖拓扑序为计划任务建立（或复用）持久化 TaskItem 映射。</summary>
    public BuildPlan LinkPlanTasks(BuildRun run, BuildPlan plan)
    {
        var linked = new Dictionary<string, TaskItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var planTask in TopologicalOrder(plan.Tasks))
        {
            var dependencies = planTask.DependsOn
                .Select(id => linked[id].Id)
                .ToArray();
            linked[planTask.Id] = FindOrCreatePlanTask(run, planTask, dependencies);
        }

        return plan with
        {
            Tasks = plan.Tasks.Select(planTask => planTask with
            {
                Status = planTask.DependsOn.Count == 0
                    ? BuildTaskStatus.InProgress
                    : BuildTaskStatus.Pending,
                Evidence = planTask.Evidence ?? [],
                TaskItemId = linked[planTask.Id].Id,
            }).ToArray(),
        };
    }

    private TaskItem FindOrCreatePlanTask(
        BuildRun run,
        BuildPlanTask planTask,
        IReadOnlyList<string> blockedBy)
    {
        var existing = taskService.ListTasks(
                conversationId: run.ConversationId?.ToString(),
                buildRunId: run.Id.ToString(),
                exactScope: true)
            .SingleOrDefault(task =>
                task.Metadata?.ExtraProperties?.TryGetValue("BuildPlanTaskId", out var mappedId) == true
                && string.Equals(mappedId, planTask.Id, StringComparison.Ordinal));
        if (existing is not null)
            return existing;

        return taskService.CreateTask(
            planTask.Title,
            planTask.Description,
            $"Executing {planTask.Title}",
            status: blockedBy.Count == 0 ? TaskStatus.InProgress : TaskStatus.Pending,
            blockedBy: blockedBy,
            metadata: new TaskMetadata(
                ExtraProperties: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["BuildPlanTaskId"] = planTask.Id,
                }),
            conversationId: run.ConversationId?.ToString(),
            buildRunId: run.Id.ToString());
    }

    public TaskItem GetLinkedTask(BuildRun run, BuildPlanTask planTask)
    {
        if (string.IsNullOrWhiteSpace(planTask.TaskItemId))
        {
            throw new InvalidOperationException(
                $"BuildPlan task '{planTask.Id}' has no persistent TaskItem mapping.");
        }

        var taskItem = taskService.GetTask(planTask.TaskItemId)
            ?? throw new InvalidOperationException(
                $"Persistent task '{planTask.TaskItemId}' for BuildPlan task '{planTask.Id}' was not found.");
        var expectedConversationId = run.ConversationId?.ToString();
        if (!string.Equals(taskItem.ConversationId, expectedConversationId, StringComparison.Ordinal)
            || !string.Equals(taskItem.BuildRunId, run.Id.ToString(), StringComparison.Ordinal)
            || taskItem.Metadata?.ExtraProperties?.TryGetValue("BuildPlanTaskId", out var mappedTaskId) != true
            || !string.Equals(mappedTaskId, planTask.Id, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Persistent task '{taskItem.Id}' does not belong to BuildPlan task '{planTask.Id}' in BuildRun '{run.Id}'.");
        }

        return taskItem;
    }

    /// <summary>恢复时按持久化任务的真实状态重投影计划任务状态；已完成的保留完成证据。</summary>
    public BuildRun ResetLinkedTasksForRecovery(BuildRun run)
    {
        if (run.Plan is null)
            return run;

        var tasks = run.Plan.Tasks.Select(planTask =>
        {
            if (string.IsNullOrWhiteSpace(planTask.TaskItemId))
                return planTask;

            var taskItem = GetLinkedTask(run, planTask);
            if (taskItem.Status == TaskStatus.Completed)
            {
                return planTask with
                {
                    Status = BuildTaskStatus.Completed,
                    Evidence = planTask.Evidence ?? [],
                };
            }

            var targetStatus = taskItem.BlockedBy.All(dependencyId =>
                taskService.GetTask(dependencyId)?.Status == TaskStatus.Completed)
                    ? TaskStatus.InProgress
                    : TaskStatus.Pending;
            var projected = taskService.ProjectTaskStatus(taskItem.Id, targetStatus);
            if (!projected.Succeeded)
            {
                throw new InvalidOperationException(
                    projected.Error
                    ?? $"Persistent task '{taskItem.Id}' could not be reset for BuildRun recovery.");
            }

            return planTask with
            {
                Status = targetStatus == TaskStatus.InProgress
                    ? BuildTaskStatus.InProgress
                    : BuildTaskStatus.Pending,
                Evidence = [],
            };
        }).ToArray();

        return run with { Plan = run.Plan with { Tasks = tasks } };
    }

    public void MarkLinkedTasksTerminal(BuildRun run, TaskStatus status)
    {
        foreach (var planTask in run.Plan?.Tasks ?? [])
        {
            if (string.IsNullOrWhiteSpace(planTask.TaskItemId))
                continue;

            var taskItem = taskService.GetTask(planTask.TaskItemId);
            if (taskItem is null
                || taskItem.Status is TaskStatus.Completed or TaskStatus.Failed or TaskStatus.Cancelled)
            {
                continue;
            }

            _ = taskService.UpdateTask(taskItem.Id, status: status);
        }
    }

    /// <summary>将链接的持久化任务标记为完成（验收证据通过时由完成门禁调用）。</summary>
    public void CompleteLinkedTask(BuildRun run, BuildPlanTask planTask)
    {
        var taskItem = GetLinkedTask(run, planTask);
        if (!taskService.UpdateTask(taskItem.Id, status: TaskStatus.Completed))
        {
            throw new InvalidOperationException(
                $"Linked task '{taskItem.Id}' for BuildPlan task '{planTask.Id}' could not be completed.");
        }
    }

    private static IReadOnlyList<BuildPlanTask> TopologicalOrder(IReadOnlyList<BuildPlanTask> tasks)
    {
        // 与原 Planning partial 实现一致：DFS 稳定拓扑序
        var byId = tasks.ToDictionary(t => t.Id, StringComparer.OrdinalIgnoreCase);
        var ordered = new List<BuildPlanTask>(tasks.Count);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Visit(BuildPlanTask task)
        {
            if (!visited.Add(task.Id))
                return;
            foreach (var dep in task.DependsOn)
            {
                if (byId.TryGetValue(dep, out var dependency))
                    Visit(dependency);
            }
            ordered.Add(task);
        }

        foreach (var task in tasks)
            Visit(task);
        return ordered;
    }
}
