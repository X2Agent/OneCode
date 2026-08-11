using OneCode.App.Services.Agent;
using OneCode.App.Services.GoalMode;
using OneCode.App.Tui;
using OneCode.Core.Build;
using OneCode.Core.Domain;
using OneCode.Core.Goals;
using OneCode.Infrastructure.Agent;
using OneCode.Infrastructure.Goals;
using System.Threading.Channels;

namespace OneCode.Tests;

public sealed class GoalCompletionServiceTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"onecode-goal-completion-{Guid.NewGuid():N}");

    public ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task Complete_ValidEvidencePublishesAndPersistsCompleted()
    {
        var store = new JsonGoalRunStore(Path.Combine(_root, "success"));
        var run = await CreateClaimedRunAsync(store, GoalRunState.Validating);
        var workspace = new CompletionWorkspaceService();
        var service = new GoalCompletionService(
            store,
            workspace,
            new CompletionStepService(semanticPassed: true));

        var completed = await service.CompleteAsync(run, 7, TestContext.Current.CancellationToken);

        completed.State.Should().Be(GoalRunState.Completed);
        completed.PublishReceipt.Should().NotBeNull();
        completed.TerminalReason.Should().Be(BuildTerminalReason.Completed);
        completed.FinalValidation.Should().Contain(gate => gate.Gate == "final-semantic-review" && gate.Passed);
        workspace.PublishCalls.Should().Be(1);
    }

    [Fact]
    public async Task Complete_MissingHardEvidenceFailsWithoutPublish()
    {
        var store = new JsonGoalRunStore(Path.Combine(_root, "failure"));
        var run = await CreateClaimedRunAsync(
            store,
            GoalRunState.Validating,
            validEvidence: false);
        var workspace = new CompletionWorkspaceService();
        var service = new GoalCompletionService(
            store,
            workspace,
            new CompletionStepService(semanticPassed: true));

        var failed = await service.CompleteAsync(run, 7, TestContext.Current.CancellationToken);

        failed.State.Should().Be(GoalRunState.Failed);
        failed.TerminalReason.Should().Be(BuildTerminalReason.ValidationFailed);
        failed.FinalValidation.Should().Contain(gate =>
            gate.Gate == "requirement-and-integration-coverage" && !gate.Passed);
        workspace.PublishCalls.Should().Be(0);
    }

    [Fact]
    public async Task Complete_PublishingStateReplaysPublishReceipt()
    {
        var store = new JsonGoalRunStore(Path.Combine(_root, "replay"));
        var run = await CreateClaimedRunAsync(store, GoalRunState.Publishing);
        var workspace = new CompletionWorkspaceService(replayed: true);
        var service = new GoalCompletionService(
            store,
            workspace,
            new CompletionStepService(semanticPassed: true));

        var completed = await service.CompleteAsync(run, 7, TestContext.Current.CancellationToken);

        completed.State.Should().Be(GoalRunState.Completed);
        completed.PublishReceipt!.Replayed.Should().BeTrue();
        workspace.PublishCalls.Should().Be(1);
    }

    private async Task<GoalRun> CreateClaimedRunAsync(
        IGoalRunStore store,
        GoalRunState state,
        bool validEvidence = true)
    {
        var step = new GoalStepSnapshot(
            1,
            "step",
            "done",
            GoalStepState.Completed,
            [],
            0,
            false,
            [],
            [],
            false,
            false,
            false);
        var evidence = new GoalStepExecutionEvidence(
            1,
            GoalStepState.Completed,
            1,
            10,
            5,
            "done",
            "accepted",
            [],
            [],
            validEvidence ? [new GoalGateEvidence("test", true, false, "passed")] : [new GoalGateEvidence("test", false, false, "failed")],
            []);
        var run = new GoalRun
        {
            Id = GoalRunId.New(),
            SessionId = SessionId.NewId(),
            Goal = "goal",
            WorkingDirectory = _root,
            WorkspaceFingerprint = "fingerprint",
            DefinitionHash = "definition",
            State = state,
            Plan = [step],
            Executions = [evidence],
            Workspace = new GoalWorkspaceSnapshot(
                "workspace",
                _root,
                _root,
                "goal-branch",
                "main",
                "base",
                "fingerprint",
                DateTimeOffset.UtcNow),
            FinalValidation = state == GoalRunState.Publishing
                ? [new GoalGateEvidence("existing", true, false, "passed")]
                : [],
        };
        await store.SaveAsync(run, 0, TestContext.Current.CancellationToken);
        var saved = await store.LoadByIdAsync(run.Id, TestContext.Current.CancellationToken)
            ?? throw new InvalidOperationException();
        return await store.ClaimWorkflowAsync(
            run.Id,
            7,
            saved.Version,
            TestContext.Current.CancellationToken);
    }

    private sealed class CompletionWorkspaceService(bool replayed = false) : IGoalWorkspaceService
    {
        public int PublishCalls { get; private set; }

        public Task<GoalWorkspaceSnapshot> PrepareAsync(GoalRun run, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<GoalStepReceipt?> FindStepReceiptAsync(GoalRun run, int goalId, long fencingToken, CancellationToken ct = default)
            => Task.FromResult<GoalStepReceipt?>(null);

        public Task<GoalStepReceipt> RecordStepAsync(GoalRun run, GoalStepExecutionEvidence evidence, long fencingToken, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<GoalPublishReceipt> PublishAsync(GoalRun run, long fencingToken, CancellationToken ct = default)
        {
            PublishCalls++;
            return Task.FromResult(new GoalPublishReceipt(
                $"goal/{run.Id}/publish",
                "commit",
                [],
                DateTimeOffset.UtcNow,
                replayed));
        }

        public Task CleanupAsync(GoalRun run, long fencingToken, CancellationToken ct = default)
            => Task.CompletedTask;
    }

    private sealed class CompletionStepService(bool semanticPassed) : IGoalStepExecutionService
    {
        public Task<SubGoalExecution> ExecuteSubGoalWithLoopStreamingAsync(
            GoalItem goal,
            GoalRunOptions options,
            EditTransaction sharedTransaction,
            ChannelWriter<TuiEvent> eventWriter,
            CancellationToken ct)
            => throw new NotSupportedException();

        public void UpdateGoalContext(GoalPlan plan, GoalItem currentGoal, IReadOnlyList<SubGoalExecution> executions, bool sharedTransactionOwned)
        {
        }

        public Task<(bool Passed, string Summary, long InputTokens, long OutputTokens)> EvaluateFinalGoalAsync(
            string originalGoal,
            IReadOnlyList<GoalItem> goals,
            IReadOnlyList<SubGoalExecution> executions,
            CancellationToken ct)
            => Task.FromResult((semanticPassed, semanticPassed ? "passed" : "failed", 2L, 1L));
    }
}
