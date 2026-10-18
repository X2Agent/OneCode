using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using OneCode.App.Services.Agent;
using OneCode.App.Services.GoalMode;
using OneCode.Core.Prompt;

namespace OneCode.Tests;

public sealed class GoalContextStateTests
{
    [Fact]
    public async Task ConcurrentAsyncFlows_DoNotOverwriteEachOther()
    {
        var state = new GoalContextState();
        var first = Snapshot(1);
        var second = Snapshot(2);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var firstTask = Task.Run(async () =>
        {
            state.Update(first);
            await gate.Task;
            return state.Snapshot;
        });
        var secondTask = Task.Run(async () =>
        {
            state.Update(second);
            gate.SetResult();
            await Task.Yield();
            return state.Snapshot;
        });

        (await firstTask).Should().Be(first);
        (await secondTask).Should().Be(second);
        state.Snapshot.Should().BeNull();
    }

    [Fact]
    public void Push_RestoresPreviousSnapshot()
    {
        var state = new GoalContextState();
        var outer = Snapshot(1);
        var inner = Snapshot(2);
        state.Update(outer);

        using (state.Push(inner))
            state.Snapshot.Should().Be(inner);

        state.Snapshot.Should().Be(outer);
    }

    [Fact]
    public void UpdateGoalContext_ProjectsCompletedAndFailedExecutionsToSnapshot()
    {
        var state = new GoalContextState();
        var executor = new GoalSubGoalExecutor(
            Substitute.For<IMainAgentRunner>(),
            Substitute.For<IChatClient>(),
            NullLoggerFactory.Instance,
            Substitute.For<IPromptManager>(),
            state,
            new GoalSubGoalGateDependencies(null, null),
            dagRunner: null!);

        var currentGoal = new GoalItem { Id = 2, Description = "Step 2" };
        var plan = new GoalPlan
        {
            Goals =
            [
                new GoalItem { Id = 1, Description = "Step 1", Status = GoalStatus.Completed },
                currentGoal,
            ],
        };
        var executions = new List<SubGoalExecution>
        {
            new(1, GoalStatus.Completed, 1, 10, 5, "Step 1 output", "ok"),
        };

        executor.UpdateGoalContext(plan, currentGoal, executions);

        state.Snapshot.Should().NotBeNull();
        state.Snapshot!.CurrentGoalId.Should().Be(2);
        state.Snapshot.CompletedSummaries.Should().ContainSingle(s => s.Id == 1 && s.Description == "Step 1");
    }

    private static GoalContextSnapshot Snapshot(int id) => new(
        id,
        2,
        [],
        []);
}
