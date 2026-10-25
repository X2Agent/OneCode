using OneCode.App.Services.Agent;
using OneCode.Core.Coordinator;
using OneCode.Core.PlanMode;

namespace OneCode.App.Services.PlanMode;

/// <summary>
/// 把计划步骤定义与 agent 待办快照投影成计划卡片的步骤列表。步骤进度的唯一事实源是待办清单
/// （<see cref="PlanTodoSeeder"/> 预置、标题带步骤 id 前缀），这里只做只读映射。
/// </summary>
public static class PlanStepProjector
{
    /// <summary>
    /// 勾选的步骤为完成；执行中「进行中」落在最后一个已勾选步骤之后的第一个未勾选步骤——
    /// 待办没有 in_progress 状态，这是勾选信号所能给出的最接近真实进度的位置。
    /// 位于最后勾选步骤之前仍未勾选的步骤（被模型移除或跳过）保持待定，不会把「进行中」永远钉在它上面。
    /// 工作流 Completed（BuildRun 已通过最终验证）时全部视为完成。
    /// </summary>
    public static IReadOnlyList<PlanStep> Project(
        PlanWorkflow workflow,
        IReadOnlyList<PlanStepDefinition>? definitions,
        IReadOnlyList<TodoListItem> todos)
    {
        if (definitions is not { Count: > 0 })
            return [new PlanStep("正在整理计划步骤", null, null, PlanStepStatus.Current)];

        var completedStepIds = todos
            .Where(todo => todo.IsComplete)
            .Select(todo => PlanTodoSeeder.TryParseStepId(todo.Title))
            .OfType<string>()
            .ToHashSet(StringComparer.Ordinal);

        var allDone = workflow.State == PlanWorkflowState.Completed;
        var lastDoneIndex = -1;
        for (var i = 0; i < definitions.Count; i++)
        {
            if (allDone || completedStepIds.Contains(definitions[i].Id))
                lastDoneIndex = i;
        }

        var executing = workflow.State == PlanWorkflowState.Executing;
        var steps = new PlanStep[definitions.Count];
        var currentAssigned = false;
        for (var i = 0; i < definitions.Count; i++)
        {
            var step = definitions[i];
            var status = PlanStepStatus.Pending;
            if (allDone || completedStepIds.Contains(step.Id))
            {
                status = PlanStepStatus.Done;
            }
            else if (executing && !currentAssigned && i > lastDoneIndex)
            {
                status = PlanStepStatus.Current;
                currentAssigned = true;
            }

            steps[i] = new PlanStep(step.Title, step.Description, null, status);
        }

        return steps;
    }
}
