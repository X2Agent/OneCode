using System.Threading.Channels;
using Microsoft.Extensions.AI;
using NSubstitute;
using OneCode.App.Services.Agent;
using OneCode.App.Services.GoalMode;
using OneCode.App.Tui;
using OneCode.Core.Build;
using OneCode.Core.Cost;
using OneCode.Core.Domain;
using OneCode.Core.Goals;
using OneCode.Infrastructure.Agent;
using OneCode.Infrastructure.Goals;

namespace OneCode.Tests;

public sealed class GoalWorkflowRuntimeTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"onecode-goal-runtime-{Guid.NewGuid():N}");

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
    public async Task PlanAndStep_PersistFencedEvidenceAndFinishAtValidating()
    {
        var store = new JsonGoalRunStore(Path.Combine(_root, "runs"));
        var run = await CreateClaimedRunAsync(store);
        var planning = new FakePlanningService();
        var steps = new FakeStepExecutionService(CompletedExecution(1));
        var workspace = new FakeWorkspaceService();
        var runtime = CreateRuntime(store, planning, steps, workspace);
        await runtime.BindAsync(run, 7, TestContext.Current.CancellationToken);

        var planned = await runtime.PlanAsync(
            new GoalWorkflowInput(run.Id, run.Goal, "model", "tools"),
            TestContext.Current.CancellationToken);
        var executed = await runtime.ExecuteNextAsync(planned, TestContext.Current.CancellationToken);
        var output = await runtime.CompleteAsync(executed, TestContext.Current.CancellationToken);

        planning.DecomposeCalls.Should().Be(1);
        steps.ExecuteCalls.Should().Be(1);
        workspace.RecordCalls.Should().Be(1);
        output.State.Should().Be(GoalRunState.Validating);
        var persisted = await store.LoadByIdAsync(run.Id, TestContext.Current.CancellationToken);
        persisted!.State.Should().Be(GoalRunState.Validating);
        persisted.Plan.Should().ContainSingle().Which.State.Should().Be(GoalStepState.Completed);
        persisted.Executions.Should().ContainSingle().Which.GoalId.Should().Be(1);
        persisted.Budget.TotalAttempts.Should().Be(1);
        persisted.Budget.TotalInputTokens.Should().Be(13, "planning and step usage must both be retained");
        persisted.Budget.TotalOutputTokens.Should().Be(7);
        persisted.WorkflowFencingToken.Should().Be(7);
    }

    [Fact]
    public async Task StepReceiptReplay_SkipsAgentAndReconcilesBusinessAggregate()
    {
        var store = new JsonGoalRunStore(Path.Combine(_root, "replay-runs"));
        var step = Snapshot(1);
        var run = await CreateClaimedRunAsync(store, [step], GoalRunState.Executing);
        var receiptEvidence = ToEvidence(CompletedExecution(1));
        var workspace = new FakeWorkspaceService(receiptEvidence);
        var steps = new FakeStepExecutionService(CompletedExecution(1));
        var runtime = CreateRuntime(store, new FakePlanningService(), steps, workspace);
        await runtime.BindAsync(run, 7, TestContext.Current.CancellationToken);
        var state = new GoalWorkflowState(
            run.Id,
            [step],
            [],
            run.Budget,
            0,
            false,
            GoalRunState.Executing);

        var result = await runtime.ExecuteNextAsync(state, TestContext.Current.CancellationToken);

        steps.ExecuteCalls.Should().Be(0, "a durable Git step receipt is authoritative during replay");
        workspace.FindCalls.Should().Be(1);
        result.CurrentIndex.Should().Be(1);
        var persisted = await store.LoadByIdAsync(run.Id, TestContext.Current.CancellationToken);
        persisted!.Executions.Should().ContainSingle().Which.Should().BeEquivalentTo(receiptEvidence);
    }

    [Fact]
    public async Task StepException_PersistsFailedBusinessTerminalState()
    {
        var store = new JsonGoalRunStore(Path.Combine(_root, "exception-runs"));
        var step = Snapshot(1);
        var run = await CreateClaimedRunAsync(store, [step], GoalRunState.Executing);
        var runtime = CreateRuntime(
            store,
            new FakePlanningService(),
            new FakeStepExecutionService(CompletedExecution(1), new InvalidOperationException("agent failed")),
            new FakeWorkspaceService());
        await runtime.BindAsync(run, 7, TestContext.Current.CancellationToken);
        var state = new GoalWorkflowState(run.Id, [step], [], run.Budget, 0, false, GoalRunState.Executing);

        var act = () => runtime.ExecuteNextAsync(state, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("agent failed");
        var persisted = await store.LoadByIdAsync(run.Id, TestContext.Current.CancellationToken);
        persisted!.State.Should().Be(GoalRunState.Failed);
        persisted.TerminalReason.Should().Be(BuildTerminalReason.AgentException);
    }

    [Fact]
    public async Task StepCancellation_LeavesBusinessRunResumable()
    {
        var store = new JsonGoalRunStore(Path.Combine(_root, "cancel-runs"));
        var step = Snapshot(1);
        var run = await CreateClaimedRunAsync(store, [step], GoalRunState.Executing);
        var runtime = CreateRuntime(
            store,
            new FakePlanningService(),
            new FakeStepExecutionService(CompletedExecution(1), new OperationCanceledException()),
            new FakeWorkspaceService());
        await runtime.BindAsync(run, 7, TestContext.Current.CancellationToken);
        var state = new GoalWorkflowState(run.Id, [step], [], run.Budget, 0, false, GoalRunState.Executing);

        var act = () => runtime.ExecuteNextAsync(state, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<OperationCanceledException>();
        var persisted = await store.LoadByIdAsync(run.Id, TestContext.Current.CancellationToken);
        persisted!.State.Should().Be(GoalRunState.Executing);
        persisted.TerminalReason.Should().BeNull();
    }

    private GoalWorkflowRuntime CreateRuntime(
        IGoalRunStore store,
        IGoalPlanningService planning,
        IGoalStepExecutionService steps,
        IGoalWorkspaceService workspace)
    {
        var cost = Substitute.For<ICostTracker>();
        cost.GetTotalCost().Returns(0m);
        return new GoalWorkflowRuntime(
            planning,
            steps,
            store,
            workspace,
            new FakeCompletionService(store),
            cost,
            new GoalWorkflowRuntimeContext(
                new GoalRunOptions
                {
                    Goal = "goal",
                    WorkingDirectory = _root,
                    ModelId = "model",
                    Tools = new List<AITool>(),
                },
                Channel.CreateUnbounded<TuiEvent>().Writer,
                static () => new EditTransaction()));
    }

    private async Task<GoalRun> CreateClaimedRunAsync(
        IGoalRunStore store,
        IReadOnlyList<GoalStepSnapshot>? plan = null,
        GoalRunState state = GoalRunState.Planning)
    {
        var run = new GoalRun
        {
            Id = GoalRunId.New(),
            SessionId = SessionId.NewId(),
            Goal = "goal",
            WorkingDirectory = _root,
            WorkspaceFingerprint = "fingerprint",
            DefinitionHash = "definition",
            State = state,
            Plan = plan ?? [],
            Workspace = new GoalWorkspaceSnapshot(
                "workspace",
                _root,
                _root,
                "goal-branch",
                "main",
                "base",
                "fingerprint",
                DateTimeOffset.UtcNow),
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

    private static GoalStepSnapshot Snapshot(int id) => new(
        id,
        $"step-{id}",
        "done",
        GoalStepState.Pending,
        [],
        0,
        false,
        [],
        [],
        false,
        false,
        false);

    private static SubGoalExecution CompletedExecution(int id) => new(
        id,
        GoalStatus.Completed,
        1,
        10,
        5,
        "done",
        "accepted",
        new SubGoalEvidence(
            "done",
            [],
            [],
            [new GoalValidationEvidence("test", true, false, "passed")],
            []));

    private static GoalStepExecutionEvidence ToEvidence(SubGoalExecution execution) => new(
        execution.GoalId,
        GoalStepState.Completed,
        execution.Attempts,
        execution.InputTokens,
        execution.OutputTokens,
        execution.AgentOutput,
        execution.Evaluation,
        execution.Evidence?.ChangedFiles ?? [],
        [],
        execution.Evidence?.Validations.Select(item => new GoalGateEvidence(
            item.Gate,
            item.Passed,
            item.Skipped,
            item.Summary)).ToArray() ?? [],
        execution.Evidence?.Diagnostics ?? []);

    private sealed class FakeCompletionService(IGoalRunStore store) : IGoalCompletionService
    {
        public async Task<GoalRun> CompleteAsync(GoalRun run, long fencingToken, CancellationToken ct)
        {
            await store.SaveFencedAsync(
                run with { State = GoalRunState.Validating },
                run.Version,
                fencingToken,
                ct);
            return await store.LoadByIdAsync(run.Id, ct) ?? throw new InvalidOperationException();
        }
    }

    private sealed class FakePlanningService : IGoalPlanningService
    {
        public int DecomposeCalls { get; private set; }

        public Task<(GoalPlan Plan, long InputTokens, long OutputTokens, string? Error, bool UsedFallback)>
            DecomposeWithFallbackAsync(string goal, string? modelId, CancellationToken ct)
        {
            DecomposeCalls++;
            return Task.FromResult((
                new GoalPlan { Goals = [ToGoalItem(Snapshot(1))] },
                3L,
                2L,
                (string?)null,
                false));
        }

        public Task<(List<GoalItem> RemainingGoals, long InputTokens, long OutputTokens)?> ReplanAsync(
            string originalGoal,
            GoalPlan currentPlan,
            int failedGoalIndex,
            IReadOnlyList<SubGoalExecution> executions,
            string? modelId,
            CancellationToken ct)
            => Task.FromResult<(List<GoalItem>, long, long)?>(null);

        public Task<(List<GoalItem> SubGoals, long InputTokens, long OutputTokens)?> DecomposeSubGoalAsync(
            GoalItem parent,
            int nextId,
            string? modelId,
            CancellationToken ct)
            => Task.FromResult<(List<GoalItem>, long, long)?>(null);
    }

    private sealed class FakeStepExecutionService(
        SubGoalExecution result,
        Exception? exception = null) : IGoalStepExecutionService
    {
        public int ExecuteCalls { get; private set; }

        public Task<SubGoalExecution> ExecuteSubGoalWithLoopStreamingAsync(
            GoalItem goal,
            GoalRunOptions options,
            EditTransaction sharedTransaction,
            ChannelWriter<TuiEvent> eventWriter,
            CancellationToken ct)
        {
            ExecuteCalls++;
            return exception is null
                ? Task.FromResult(result)
                : Task.FromException<SubGoalExecution>(exception);
        }

        public void UpdateGoalContext(
            GoalPlan plan,
            GoalItem currentGoal,
            IReadOnlyList<SubGoalExecution> executions,
            bool sharedTransactionOwned)
        {
        }

        public Task<(bool Passed, string Summary, long InputTokens, long OutputTokens)> EvaluateFinalGoalAsync(
            string originalGoal,
            IReadOnlyList<GoalItem> goals,
            IReadOnlyList<SubGoalExecution> executions,
            CancellationToken ct)
            => Task.FromResult((true, "passed", 0L, 0L));
    }

    private sealed class FakeWorkspaceService(GoalStepExecutionEvidence? replay = null) : IGoalWorkspaceService
    {
        private GoalStepExecutionEvidence? _receipt = replay;
        public int FindCalls { get; private set; }
        public int RecordCalls { get; private set; }

        public Task<GoalWorkspaceSnapshot> PrepareAsync(GoalRun run, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<GoalStepReceipt?> FindStepReceiptAsync(
            GoalRun run,
            int goalId,
            long fencingToken,
            CancellationToken ct = default)
        {
            FindCalls++;
            return Task.FromResult(_receipt is null
                ? null
                : new GoalStepReceipt("operation", goalId, "commit", _receipt, true, DateTimeOffset.UtcNow));
        }

        public Task<GoalStepReceipt> RecordStepAsync(
            GoalRun run,
            GoalStepExecutionEvidence evidence,
            long fencingToken,
            CancellationToken ct = default)
        {
            RecordCalls++;
            _receipt = evidence;
            return Task.FromResult(new GoalStepReceipt(
                "operation",
                evidence.GoalId,
                "commit",
                evidence,
                false,
                DateTimeOffset.UtcNow));
        }

        public Task<GoalPublishReceipt> PublishAsync(GoalRun run, long fencingToken, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task CleanupAsync(GoalRun run, long fencingToken, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    private static GoalItem ToGoalItem(GoalStepSnapshot item) => new()
    {
        Id = item.Id,
        Description = item.Description,
        SuccessCriteria = item.SuccessCriteria,
        Status = GoalStatus.Pending,
        RequiredTools = item.RequiredTools,
        Depth = item.Depth,
        NeedsFurtherDecomposition = item.NeedsFurtherDecomposition,
        ExpectedFiles = item.ExpectedFiles,
        AllowedPaths = item.AllowedPaths,
        RequiresBuild = item.RequiresBuild,
        RequiresTests = item.RequiresTests,
        Optional = item.Optional,
    };
}
