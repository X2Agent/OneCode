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

    [Fact]
    public async Task PlanFallback_ContinuesWithSingleGoalExecution()
    {
        var store = new JsonGoalRunStore(Path.Combine(_root, "fallback-runs"));
        var run = await CreateClaimedRunAsync(store);
        var planning = new FakePlanningService { UsedFallback = true, Error = "model unavailable" };
        var runtime = CreateRuntime(store, planning, new FakeStepExecutionService(CompletedExecution(1)), new FakeWorkspaceService());
        await runtime.BindAsync(run, 7, TestContext.Current.CancellationToken);

        var planned = await runtime.PlanAsync(
            new GoalWorkflowInput(run.Id, run.Goal, "model", "tools"),
            TestContext.Current.CancellationToken);

        planned.Plan.Should().ContainSingle();
        var persisted = await store.LoadByIdAsync(run.Id, TestContext.Current.CancellationToken);
        persisted!.State.Should().Be(GoalRunState.Executing);
        persisted.FailureSummary.Should().BeNull();
        persisted.Plan.Should().ContainSingle().Which.State.Should().Be(GoalStepState.Pending);
    }

    [Fact]
    public async Task FailedStep_RecordsRolledBackEvidenceWithoutChangedFiles()
    {
        var store = new JsonGoalRunStore(Path.Combine(_root, "h4-runs"));
        var step = Snapshot(1);
        var run = await CreateClaimedRunAsync(store, [step], GoalRunState.Executing);
        var failedExecution = new SubGoalExecution(
            1,
            GoalStatus.Failed,
            2,
            10,
            5,
            "partial",
            "acceptance not met",
            new SubGoalEvidence(
                "partial",
                [Path.Combine(_root, "outside", "file.cs")],
                [],
                [new GoalValidationEvidence("test", false, false, "failed")],
                []));
        var workspace = new FakeWorkspaceService();
        var runtime = CreateRuntime(
            store,
            new FakePlanningService(),
            new FakeStepExecutionService(failedExecution),
            workspace);
        await runtime.BindAsync(run, 7, TestContext.Current.CancellationToken);
        var state = new GoalWorkflowState(run.Id, [step], [], run.Budget, 0, false, GoalRunState.Executing);

        await runtime.ExecuteNextAsync(state, TestContext.Current.CancellationToken);

        // H4: 失败步骤的文件改动已回滚，回执不得再声称改过文件（否则 change-scope 门禁误杀）。
        workspace.LastRecordedEvidence!.ChangedFiles.Should().BeEmpty();
        var persisted = await store.LoadByIdAsync(run.Id, TestContext.Current.CancellationToken);
        persisted!.Executions.Should().ContainSingle()
            .Which.ChangedFiles.Should().BeEmpty();
        persisted.Plan.Should().ContainSingle()
            .Which.State.Should().Be(GoalStepState.Failed);
    }

    [Fact]
    public async Task ExecuteNext_FirstStep_SkipsWhenAttemptBudgetInsufficient()
    {
        // Fix-7/N-01：守卫不再依赖 CurrentIndex > 0——resume 到 index 0 时同样受剩余额度约束。
        var store = new JsonGoalRunStore(Path.Combine(_root, "first-step-guard"));
        var step = Snapshot(1);
        var run = await CreateClaimedRunAsync(store, [step], GoalRunState.Executing);
        var steps = new FakeStepExecutionService(CompletedExecution(1));
        var workspace = new FakeWorkspaceService();
        var (runtime, _) = CreateRuntimeWithEvents(
            store,
            new FakePlanningService(),
            steps,
            workspace,
            new GoalBudget { MaxSubGoalAttempts = 2 });
        await runtime.BindAsync(run, 7, TestContext.Current.CancellationToken);
        var state = new GoalWorkflowState(run.Id, [step], [], run.Budget, 0, false, GoalRunState.Executing);

        var result = await runtime.ExecuteNextAsync(state, TestContext.Current.CancellationToken);

        steps.ExecuteCalls.Should().Be(0, "the remaining attempt budget cannot safely start a sub-goal");
        workspace.RecordCalls.Should().Be(1);
        result.Plan.Should().ContainSingle().Which.State.Should().Be(GoalStepState.Skipped);
        workspace.LastRecordedEvidence!.Validations.Should()
            .Contain(gate => gate.Gate == "budget" && gate.Skipped);
    }

    [Fact]
    public async Task ApplyEvidence_NeverNegativeDeltaFromStaleEvidence()
    {
        // Fix-2/F-02：旧证据 Attempts/Tokens > 0、新证据为 0 时，预算消耗不得回退。
        var store = new JsonGoalRunStore(Path.Combine(_root, "negative-delta"));
        var step = Snapshot(1);
        var previous = new GoalStepExecutionEvidence(
            1, GoalStepState.Failed, 3, 30, 15, "old", "old-eval", [], [],
            [new GoalGateEvidence("test", false, false, "failed")], []);
        var replay = new GoalStepExecutionEvidence(
            1, GoalStepState.Completed, 0, 0, 0, string.Empty, string.Empty, [], [], [], []);
        var run = await CreateClaimedRunWithBudgetAsync(
            store,
            [step],
            new GoalBudgetSnapshot(3, 30, 15, 0m, DateTimeOffset.UtcNow));
        var runtime = CreateRuntime(
            store,
            new FakePlanningService(),
            new FakeStepExecutionService(CompletedExecution(1)),
            new FakeWorkspaceService(replay));
        await runtime.BindAsync(run, 7, TestContext.Current.CancellationToken);
        var state = new GoalWorkflowState(
            run.Id,
            [step],
            [previous],
            run.Budget,
            0,
            false,
            GoalRunState.Executing);
        await runtime.ExecuteNextAsync(state, TestContext.Current.CancellationToken);

        var persisted = await store.LoadByIdAsync(run.Id, TestContext.Current.CancellationToken);
        persisted!.Budget.TotalAttempts.Should().Be(3, "a zero-attempt replacement must not subtract prior attempts");
        persisted.Budget.TotalInputTokens.Should().Be(30);
        persisted.Budget.TotalOutputTokens.Should().Be(15);
    }

    [Fact]
    public async Task ExecuteNext_PublishesBudgetWarningOncePerLevelChange()
    {
        // Fix-6/N-03：EvaluateWarning 结果必须发布到 EventWriter，且级别不变时不重复发布。
        var store = new JsonGoalRunStore(Path.Combine(_root, "warning-events"));
        var plan = new[] { Snapshot(1), Snapshot(2) };
        // 14/20 = 70% → Early（黄色）。
        var run = await CreateClaimedRunWithBudgetAsync(
            store,
            plan,
            new GoalBudgetSnapshot(14, 0, 0, 0m, DateTimeOffset.UtcNow));
        var (runtime, events) = CreateRuntimeWithEvents(
            store,
            new FakePlanningService(),
            new FakeStepExecutionService(CompletedExecution(1)),
            new FakeWorkspaceService());
        await runtime.BindAsync(run, 7, TestContext.Current.CancellationToken);
        var state = new GoalWorkflowState(run.Id, plan, [], run.Budget, 0, false, GoalRunState.Executing);

        var first = await runtime.ExecuteNextAsync(state, TestContext.Current.CancellationToken);
        await runtime.ExecuteNextAsync(first, TestContext.Current.CancellationToken);

        var published = new List<TuiEvent>();
        while (events.Reader.TryRead(out var evt))
            published.Add(evt);
        published.OfType<TuiGoalBudgetWarning>().Should().ContainSingle()
            .Which.Level.Should().Be(GoalBudgetWarningLevel.Early);

        var persisted = await store.LoadByIdAsync(run.Id, TestContext.Current.CancellationToken);
        persisted!.Budget.LastActivityAt.Should().NotBeNull("wall clock tracking must stamp activity");
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

    private (GoalWorkflowRuntime Runtime, Channel<TuiEvent> Events) CreateRuntimeWithEvents(
        IGoalRunStore store,
        IGoalPlanningService planning,
        IGoalStepExecutionService steps,
        IGoalWorkspaceService workspace,
        GoalBudget? budget = null)
    {
        var cost = Substitute.For<ICostTracker>();
        cost.GetTotalCost().Returns(0m);
        var events = Channel.CreateUnbounded<TuiEvent>();
        var options = new GoalRunOptions
        {
            Goal = "goal",
            WorkingDirectory = _root,
            ModelId = "model",
            Tools = new List<AITool>(),
            Budget = budget,
        };
        var runtime = new GoalWorkflowRuntime(
            planning,
            steps,
            store,
            workspace,
            new FakeCompletionService(store),
            cost,
            new GoalWorkflowRuntimeContext(
                options,
                events.Writer,
                static () => new EditTransaction()));
        return (runtime, events);
    }

    private async Task<GoalRun> CreateClaimedRunWithBudgetAsync(
        IGoalRunStore store,
        IReadOnlyList<GoalStepSnapshot> plan,
        GoalBudgetSnapshot budget)
    {
        var run = new GoalRun
        {
            Id = GoalRunId.New(),
            SessionId = SessionId.NewId(),
            Goal = "goal",
            WorkingDirectory = _root,
            WorkspaceFingerprint = "fingerprint",
            DefinitionHash = "definition",
            State = GoalRunState.Executing,
            Plan = plan,
            Budget = budget,
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
        public bool UsedFallback { get; set; }
        public string? Error { get; set; }

        public Task<(GoalPlan Plan, long InputTokens, long OutputTokens, string? Error, bool UsedFallback)>
            DecomposeWithFallbackAsync(string goal, string? modelId, CancellationToken ct)
        {
            DecomposeCalls++;
            return Task.FromResult((
                new GoalPlan { Goals = [ToGoalItem(Snapshot(1))] },
                3L,
                2L,
                Error,
                UsedFallback));
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
        public GoalStepExecutionEvidence? LastRecordedEvidence { get; private set; }

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
            LastRecordedEvidence = evidence;
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
