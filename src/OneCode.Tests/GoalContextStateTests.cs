using OneCode.App.Services.GoalMode;

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

    private static GoalContextSnapshot Snapshot(int id) => new(
        id,
        2,
        [],
        [],
        true);
}
