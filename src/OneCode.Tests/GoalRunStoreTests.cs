using OneCode.Core.Build;
using OneCode.Core.Domain;
using OneCode.Core.Goals;
using OneCode.Infrastructure.Goals;

namespace OneCode.Tests;

public sealed class GoalRunStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "onecode-goal-run-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task SaveAndLoad_UsesOptimisticConcurrencyAndSessionIdentity()
    {
        var store = CreateStore();
        var run = CreateRun();

        await store.SaveAsync(run, 0, TestContext.Current.CancellationToken);
        var saved = await store.LoadBySessionAsync(run.SessionId, TestContext.Current.CancellationToken);

        saved.Should().NotBeNull();
        saved!.Version.Should().Be(1);
        saved.SequenceNumber.Should().Be(1);
        var stale = () => store.SaveAsync(saved with { FailureSummary = "stale" }, 0, TestContext.Current.CancellationToken);
        await stale.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Concurrency*");
        var otherRun = run with { Id = GoalRunId.New() };
        var conflict = () => store.SaveAsync(otherRun, 1, TestContext.Current.CancellationToken);
        await conflict.Should().ThrowAsync<InvalidDataException>().WithMessage("*already belongs*");
    }

    [Fact]
    public async Task WorkflowClaim_RequiresMonotonicTokenAndFencesAllLaterWrites()
    {
        var store = CreateStore();
        var run = CreateRun();
        await store.SaveAsync(run, 0, TestContext.Current.CancellationToken);

        var claimed = await store.ClaimWorkflowAsync(run.Id, 7, 1, TestContext.Current.CancellationToken);
        claimed.WorkflowFencingToken.Should().Be(7);

        var unfenced = () => store.SaveAsync(claimed with { State = GoalRunState.Executing }, claimed.Version, TestContext.Current.CancellationToken);
        await unfenced.Should().ThrowAsync<InvalidOperationException>().WithMessage("*fencing*");
        var stale = () => store.SaveFencedAsync(
            claimed with { State = GoalRunState.Executing }, claimed.Version, 6, TestContext.Current.CancellationToken);
        await stale.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Stale*");

        await store.SaveFencedAsync(
            claimed with { State = GoalRunState.Executing }, claimed.Version, 7, TestContext.Current.CancellationToken);
        var current = await store.LoadByIdAsync(run.Id, TestContext.Current.CancellationToken);
        current!.State.Should().Be(GoalRunState.Executing);
        var oldClaim = () => store.ClaimWorkflowAsync(run.Id, 7, current.Version, TestContext.Current.CancellationToken);
        await oldClaim.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Stale*");
    }

    [Fact]
    public async Task CompletedRun_RequiresValidationPublishAndCompletedRequiredSteps()
    {
        var store = CreateStore();
        var run = CreateRun();

        var invalid = run with { State = GoalRunState.Completed };
        var act = () => store.SaveAsync(invalid, 0, TestContext.Current.CancellationToken);
        await act.Should().ThrowAsync<InvalidDataException>().WithMessage("*publish receipt*");

        var completed = run with
        {
            State = GoalRunState.Completed,
            Plan = [run.Plan[0] with { State = GoalStepState.Completed }],
            FinalValidation = [new GoalGateEvidence("final", true, false, "passed")],
            PublishReceipt = new GoalPublishReceipt("op-1", "hash-1", [], DateTimeOffset.UtcNow),
            TerminalReason = BuildTerminalReason.Completed,
        };
        await store.SaveAsync(completed, 0, TestContext.Current.CancellationToken);
        var saved = await store.LoadByIdAsync(run.Id, TestContext.Current.CancellationToken);
        saved!.IsTerminal.Should().BeTrue();
        saved.CompletedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task TerminalRun_IsImmutable()
    {
        var store = CreateStore();
        var run = CreateRun() with
        {
            State = GoalRunState.Failed,
            TerminalReason = BuildTerminalReason.ValidationFailed,
            FailureSummary = "failed",
        };
        await store.SaveAsync(run, 0, TestContext.Current.CancellationToken);
        var terminal = await store.LoadByIdAsync(run.Id, TestContext.Current.CancellationToken);

        var act = () => store.SaveAsync(
            terminal! with { FailureSummary = "changed" },
            terminal!.Version,
            TestContext.Current.CancellationToken);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*immutable*");
    }

    [Fact]
    public async Task ListActive_IgnoresTerminalRuns()
    {
        var store = CreateStore();
        var active = CreateRun();
        var terminal = CreateRun() with
        {
            SessionId = SessionId.NewId(),
            State = GoalRunState.Cancelled,
            TerminalReason = BuildTerminalReason.Cancelled,
        };
        await store.SaveAsync(active, 0, TestContext.Current.CancellationToken);
        await store.SaveAsync(terminal, 0, TestContext.Current.CancellationToken);

        var results = await store.ListActiveAsync(TestContext.Current.CancellationToken);
        results.Should().ContainSingle().Which.Id.Should().Be(active.Id);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }

    private JsonGoalRunStore CreateStore() => new(_root);

    private static GoalRun CreateRun()
    {
        var now = DateTimeOffset.UtcNow;
        return new GoalRun
        {
            Id = GoalRunId.New(),
            SessionId = SessionId.NewId(),
            Goal = "Implement and verify the requested change.",
            WorkingDirectory = Path.GetTempPath(),
            WorkspaceFingerprint = "workspace-v1",
            DefinitionHash = "definition-v1",
            Plan =
            [
                new GoalStepSnapshot(
                    1,
                    "Implement",
                    "Tests pass",
                    GoalStepState.Pending,
                    [],
                    0,
                    false,
                    [],
                    [],
                    true,
                    true,
                    false),
            ],
            Budget = new GoalBudgetSnapshot(0, 0, 0, 0m, now),
            CreatedAt = now,
            UpdatedAt = now,
        };
    }
}
