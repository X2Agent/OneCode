using OneCode.Core.Build;
using OneCode.Core.PlanMode;
using OneCode.Core.Tasks;
using TaskStatus = OneCode.Core.Tasks.TaskStatus;

namespace OneCode.App.Services.BuildMode;

/// <summary>
/// BuildPlan/Plan step 与持久化 TaskItem 之间的单一链接器（收敛：
/// 合并 Build 侧建链与 Plan 执行工具的两份平行投影实现，同一映射键、同一投影函数）。
/// 拥有 ITaskService 依赖，是计划任务与任务系统之间的唯一通道——协调器、工具与其余
/// partial 不直接触碰 ITaskService。拓扑序建链、恢复重置、终态标记、step 投影对账。
/// </summary>
public sealed class BuildTaskLinker(ITaskService taskService)
{
    /// <summary>计划任务映射键：TaskItem.Metadata.ExtraProperties 中的计划任务 Id。</summary>
    public const string PlanTaskIdKey = "BuildPlanTaskId";

    /// <summary>按依赖拓扑序为计划任务建立（或复用）持久化 TaskItem 映射。</summary>
    public BuildPlan LinkPlanTasks(BuildRun run, BuildPlan plan)
    {
        var linked = new Dictionary<string, TaskItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var planTask in WorkflowTopology.DepthFirstOrder(
            plan.Tasks, task => task.Id, task => task.DependsOn).Ordered)
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

    /// <summary>按映射键查找已链接的持久化任务；无匹配返回 null。</summary>
    public TaskItem? TryFindLinkedTask(string? conversationId, string buildRunId, string planTaskId)
        => taskService.ListTasks(
                conversationId: conversationId,
                buildRunId: buildRunId,
                exactScope: true)
            .SingleOrDefault(item =>
                item.Metadata?.ExtraProperties?.TryGetValue(PlanTaskIdKey, out var mappedId) == true
                && string.Equals(mappedId, planTaskId, StringComparison.Ordinal));

    private TaskItem FindOrCreatePlanTask(
        BuildRun run,
        BuildPlanTask planTask,
        IReadOnlyList<string> blockedBy)
    {
        var existing = TryFindLinkedTask(run.ConversationId?.ToString(), run.Id.ToString(), planTask.Id);
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
            || taskItem.Metadata?.ExtraProperties?.TryGetValue(PlanTaskIdKey, out var mappedTaskId) != true
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

    /// <summary>按映射键查找 Plan step 对应的持久化任务；未建链即 fail-closed（Plan 执行工具既有语义）。</summary>
    public TaskItem GetLinkedPlanTask(string conversationId, string buildRunId, string stepId)
        => TryFindLinkedTask(conversationId, buildRunId, stepId)
           ?? throw new PlanTransitionException(
               $"Approved plan step '{stepId}' has no persistent Build task mapping.");

    /// <summary>
    /// Plan 执行变更前对账：按拓扑序把 PlanWorkflow 的全部 step 执行投影到持久化任务
    /// （收敛自 PlanExecutionTool.ReconcileLinkedBuildTasks，同一映射键与投影语义）。
    /// </summary>
    public void ReconcilePlanStepProjections(string conversationId, string buildRunId, PlanWorkflow workflow)
    {
        var byId = workflow.StepExecutions.ToDictionary(
            execution => execution.StepId,
            StringComparer.Ordinal);
        var orderedIds = TopologicalPlanStepIds(workflow);
        foreach (var stepId in orderedIds)
        {
            var execution = byId[stepId];
            var task = GetLinkedPlanTask(conversationId, buildRunId, execution.StepId);
            var projectionKey = $"{workflow.Id}:{workflow.Version}:{execution.StepId}";
            ProjectPlanStep(task, execution, projectionKey);
        }
    }

    /// <summary>Plan step 推进前的未完成依赖门（收敛自 PlanExecutionTool.ValidateLinkedTaskDependencies）。</summary>
    public void ValidatePlanStepDependencies(TaskItem task, PlanStepExecutionStatus status)
    {
        if (status is not (PlanStepExecutionStatus.InProgress or PlanStepExecutionStatus.Completed))
            return;

        var unresolved = task.BlockedBy
            .Where(dependencyId => taskService.GetTask(dependencyId)?.Status != TaskStatus.Completed)
            .ToArray();
        if (unresolved.Length > 0)
        {
            throw new PlanTransitionException(
                $"Approved plan step cannot advance because persistent Build task '{task.Id}' is blocked by: {string.Join(", ", unresolved)}.");
        }
    }

    /// <summary>Plan step 状态投影（收敛自 PlanExecutionTool.ProjectLinkedBuildTask，语义逐字保留）。</summary>
    public void ProjectPlanStep(TaskItem task, PlanStepExecution execution, string projectionKey)
    {
        var taskStatus = execution.Status switch
        {
            PlanStepExecutionStatus.Pending => TaskStatus.Pending,
            PlanStepExecutionStatus.InProgress => TaskStatus.InProgress,
            PlanStepExecutionStatus.Completed => TaskStatus.Completed,
            PlanStepExecutionStatus.Failed => TaskStatus.Failed,
            PlanStepExecutionStatus.Skipped => TaskStatus.Completed,
            PlanStepExecutionStatus.Cancelled => TaskStatus.Cancelled,
            _ => throw new PlanTransitionException($"Unsupported plan step status '{execution.Status}'."),
        };
        var output = execution.Status == PlanStepExecutionStatus.Failed
            ? execution.Error
            : execution.Evidence;
        var projected = taskService.ProjectTaskStatus(
            task.Id,
            taskStatus,
            output,
            projectionKey,
            requireCompletedDependencies: execution.Status is
                PlanStepExecutionStatus.InProgress or PlanStepExecutionStatus.Completed);
        if (!projected.Succeeded)
        {
            throw new PlanTransitionException(
                projected.Error
                ?? $"Persistent Build task '{task.Id}' could not project plan step state '{execution.Status}'.");
        }
    }

    /// <summary>Plan step 拓扑序（收敛自 PlanExecutionTool.TopologicalStepIds，含未定义 step fail-closed）。</summary>
    private static IReadOnlyList<string> TopologicalPlanStepIds(PlanWorkflow workflow)
    {
        var definitions = workflow.ApprovedSnapshot?.Steps.ToDictionary(
            step => step.Id,
            StringComparer.Ordinal)
            ?? throw new PlanTransitionException(
                $"Plan '{workflow.Id}' has no approved step definitions for Build task reconciliation.");

        // 执行引用的 step 必须已定义（与原 Visit 抛错语义一致）
        foreach (var execution in workflow.StepExecutions)
        {
            if (!definitions.ContainsKey(execution.StepId))
                throw new PlanTransitionException(
                    $"Approved plan step '{execution.StepId}' was not found during reconciliation.");
        }

        var result = WorkflowTopology.DepthFirstOrder(
            workflow.StepExecutions,
            execution => execution.StepId,
            execution => definitions.TryGetValue(execution.StepId, out var definition)
                ? definition.DependsOn
                : []);
        if (result.MissingDependencies is { Count: > 0 })
            throw new PlanTransitionException(
                $"Approved plan step '{result.MissingDependencies[0]}' was not found during reconciliation.");
        return result.Ordered.Select(execution => execution.StepId).ToArray();
    }
}