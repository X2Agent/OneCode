using OneCode.App.Services.Agent;
using OneCode.App.Services.PlanMode;
using OneCode.Core.Build;
using OneCode.Core.Coordinator;
using OneCode.Core.PlanMode;

namespace OneCode.Tests;

/// <summary>
/// 计划卡片步骤投影：步骤进度只读自 agent 待办（经 <see cref="PlanTodoSeeder.FormatTitle"/> 写入的标题），
/// 「进行中」落在最后一个已勾选步骤之后。
/// </summary>
public sealed class PlanStepProjectorTests
{
    private static readonly string[] StepIds = ["s1", "s2", "s3", "s4"];

    private static PlanWorkflow Workflow(PlanWorkflowState state) =>
        PlanWorkflow.Create(SessionId.NewId()) with { State = state };

    private static PlanStepDefinition Definition(string id) => new()
    {
        Id = id,
        Title = $"Title {id}",
        Description = $"Do {id}",
        Files = [],
        AcceptanceCriteria = ["done"],
        DependsOn = [],
        Risk = PlanStepRisk.Low,
    };

    /// <summary>走真实写入端的标题格式，而不是手写字符串。</summary>
    private static TodoListItem Todo(int numericId, string stepId, bool complete) =>
        new(numericId, PlanTodoSeeder.FormatTitle(
            new BuildPlanTask(stepId, $"Title {stepId}", $"Do {stepId}", [], [], ["done"])), null, complete);

    private static PlanStepStatus[] Statuses(PlanWorkflowState state, params TodoListItem[] todos) =>
        PlanStepProjector.Project(Workflow(state), StepIds.Select(Definition).ToList(), todos)
            .Select(step => step.Status).ToArray();

    [Fact]
    public void Project_ExecutingWithNothingTicked_MarksFirstStepCurrent()
    {
        Statuses(PlanWorkflowState.Executing).Should().Equal(
            PlanStepStatus.Current, PlanStepStatus.Pending, PlanStepStatus.Pending, PlanStepStatus.Pending);
    }

    [Fact]
    public void Project_ExecutingWithPrefixTicked_MarksNextStepCurrent()
    {
        Statuses(PlanWorkflowState.Executing, Todo(1, "s1", true), Todo(2, "s2", true), Todo(3, "s3", false), Todo(4, "s4", false))
            .Should().Equal(PlanStepStatus.Done, PlanStepStatus.Done, PlanStepStatus.Current, PlanStepStatus.Pending);
    }

    [Fact]
    public void Project_SkippedEarlierStep_DoesNotPinCurrentToIt()
    {
        // s1 被模型移除（没有对应待办）、s2 已勾选：进行中应是 s3，而不是永远停在 s1。
        Statuses(PlanWorkflowState.Executing, Todo(2, "s2", true), Todo(3, "s3", false), Todo(4, "s4", false))
            .Should().Equal(PlanStepStatus.Pending, PlanStepStatus.Done, PlanStepStatus.Current, PlanStepStatus.Pending);
    }

    [Fact]
    public void Project_AllStepsTicked_HasNoCurrentStep()
    {
        Statuses(
                PlanWorkflowState.Executing,
                Todo(1, "s1", true), Todo(2, "s2", true), Todo(3, "s3", true), Todo(4, "s4", true))
            .Should().OnlyContain(status => status == PlanStepStatus.Done);
    }

    [Fact]
    public void Project_CompletedWorkflow_MarksEverythingDoneEvenWithoutTodos()
    {
        Statuses(PlanWorkflowState.Completed).Should().OnlyContain(status => status == PlanStepStatus.Done);
    }

    [Fact]
    public void Project_NotExecuting_NeverMarksCurrent()
    {
        Statuses(PlanWorkflowState.StartingExecution).Should().OnlyContain(status => status == PlanStepStatus.Pending);
    }

    [Fact]
    public void Project_ModelAddedTodo_IsNotMistakenForAPlanStep()
    {
        var own = new TodoListItem(9, "Note: s1 looks fine", null, true);

        Statuses(PlanWorkflowState.Executing, own).Should().Equal(
            PlanStepStatus.Current, PlanStepStatus.Pending, PlanStepStatus.Pending, PlanStepStatus.Pending);
    }
}
