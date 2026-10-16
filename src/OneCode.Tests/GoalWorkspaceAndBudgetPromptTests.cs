using System.Threading.Channels;
using Microsoft.Extensions.AI;
using NSubstitute;
using OneCode.App.Services;
using OneCode.App.Services.Agent;
using OneCode.App.Services.GoalMode;
using OneCode.App.Session;
using OneCode.App.Tui;
using OneCode.Core.Config;
using OneCode.Core.Domain;
using OneCode.Core.Goals;
using OneCode.Core.Workflows;
using OneCode.Infrastructure.Agent;
using OneCode.Infrastructure.Goals;

namespace OneCode.Tests;

public sealed class GoalWorkspaceAndBudgetPromptTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"onecode-goal-wb-{Guid.NewGuid():N}");

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
    public void ResolveEffectiveWorkingDirectory_StartedInSubdirectory_MapsToWorktreeSubdirectoryAndCreatesIt()
    {
        var repoRoot = Path.Combine(_root, "repo");
        var subDir = Path.Combine(repoRoot, ".dev-workspace");
        var worktreeRoot = Path.Combine(_root, "repo.worktree", "html");
        Directory.CreateDirectory(subDir);
        Directory.CreateDirectory(worktreeRoot);

        var run = new GoalRun
        {
            Id = GoalRunId.New(),
            SessionId = SessionId.NewId(),
            Goal = "build html",
            WorkingDirectory = subDir,
            WorkspaceFingerprint = "fp",
            DefinitionHash = "hash",
            Workspace = new GoalWorkspaceSnapshot(
                "ws-1",
                repoRoot,
                worktreeRoot,
                "onecode/goal/html",
                "main",
                "abc123",
                "fp",
                DateTimeOffset.UtcNow),
        };

        var effective = GoalWorkspacePathResolver.ResolveEffectiveWorkingDirectory(run);

        var expected = Path.Combine(worktreeRoot, ".dev-workspace");
        effective.Should().Be(expected, "Goal started inside a subdirectory must map the same relative offset into the worktree");
        Directory.Exists(expected).Should().BeTrue("mapped subdirectory inside worktree must be created so tools can write into '.'");
        GoalWorkspacePathResolver.ResolveWorktreeRelativePath(run, "justin-sun-timeline.html")
            .Should().Be(".dev-workspace/justin-sun-timeline.html");
        GoalWorkspacePathResolver.ResolveWorktreeRelativePath(run, Path.Combine(expected, "justin-sun-timeline.html"))
            .Should().Be(".dev-workspace/justin-sun-timeline.html");
    }

    [Fact]
    public async Task ExecuteNextAsync_RecordStepFailureWithoutReconciledReceipt_PreservesWorktreeFilesInsteadOfRollingBack()
    {
        var store = new JsonGoalRunStore(Path.Combine(_root, "runs-preserve"));
        var step = CreateStep(1);
        var run = await CreateClaimedRunAsync(store, [step], totalAttempts: 0);
        var deliverablePath = Path.Combine(run.Workspace!.IsolatedPath, "justin-sun-timeline.html");

        var stepExecutor = new FakeStepExecutionService(tx =>
        {
            tx.Snapshot(deliverablePath);
            File.WriteAllText(deliverablePath, "<html>deliverable</html>");
        });

        var workspace = Substitute.For<IGoalWorkspaceService>();
        workspace.FindStepReceiptAsync(Arg.Any<GoalRun>(), Arg.Any<int>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns((GoalStepReceipt?)null);
        workspace.RecordStepAsync(Arg.Any<GoalRun>(), Arg.Any<GoalStepExecutionEvidence>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns<GoalStepReceipt>(_ => throw new InvalidOperationException("Git command failed: git add"));

        var context = new GoalWorkflowRuntimeContext(
            new GoalRunOptions
            {
                Goal = "test",
                WorkingDirectory = _root,
                Tools = new List<AITool>(),
            },
            Channel.CreateUnbounded<TuiEvent>().Writer,
            static () => new EditTransaction());
        var runtime = new GoalWorkflowRuntime(
            new FakePlanningService(),
            stepExecutor,
            store,
            workspace,
            new FakeCompletionService(),
            context);
        await runtime.BindAsync(run, 7, TestContext.Current.CancellationToken);
        var state = new GoalWorkflowState(run.Id, [step], [], run.Budget, 0, false, GoalRunState.Executing);

        var act = () => runtime.ExecuteNextAsync(state, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Git command failed*");
        File.Exists(deliverablePath).Should().BeTrue(
            "when RecordStepAsync fails after the sub-goal produced files, the transaction must preserve worktree files instead of deleting them on Dispose");
    }

    [Fact]
    public void SessionWorkingDirectoryAccessor_WithWorkingDirectoryOverride_ReturnsOverrideAndRestoresOnDispose()
    {
        var sessionManager = Substitute.For<ISessionWorkingDirectory>();
        sessionManager.WorkingDirectory.Returns(@"C:\repo\.dev-workspace");
        var configManager = Substitute.For<IConfigManager>();
        var sut = new SessionWorkingDirectoryAccessor(sessionManager, configManager);

        sut.WorkingDirectory.Should().Be(@"C:\repo\.dev-workspace");

        using (OneCodeAgentRunContext.UseWorkingDirectory(@"C:\repo.worktree\html\.dev-workspace"))
        {
            sut.WorkingDirectory.Should().Be(@"C:\repo.worktree\html\.dev-workspace",
                "singleton tools reading IWorkingDirectoryAccessor must observe the scoped worktree directory");
        }

        sut.WorkingDirectory.Should().Be(@"C:\repo\.dev-workspace",
            "disposing the scope must restore the session working directory");
    }

    [Fact]
    public async Task ExecuteNextAsync_BudgetExhausted_ContinueWithExtraBudget_ExtendsBudgetAndExecutesStep()
    {
        var store = new JsonGoalRunStore(Path.Combine(_root, "runs-continue"));
        var step = CreateStep(1);
        var run = await CreateClaimedRunAsync(store, [step], totalAttempts: 5);
        var channel = Channel.CreateUnbounded<TuiEvent>();
        var runtime = CreateRuntimeWithPrompt(store, channel.Writer, maxAttempts: 5);
        await runtime.BindAsync(run, 7, TestContext.Current.CancellationToken);
        var state = new GoalWorkflowState(run.Id, [step], [], run.Budget, 0, false, GoalRunState.Executing);

        var responder = Task.Run(async () =>
        {
            await foreach (var evt in channel.Reader.ReadAllAsync(TestContext.Current.CancellationToken))
            {
                if (evt is TuiGoalBudgetExhaustedRequest req)
                {
                    req.ResponseSource.TrySetResult(GoalBudgetDecision.ContinueWithExtraBudget);
                    break;
                }
            }
        }, TestContext.Current.CancellationToken);

        var next = await runtime.ExecuteNextAsync(state, TestContext.Current.CancellationToken);
        await responder;

        next.CurrentIndex.Should().Be(1, "selecting ContinueWithExtraBudget must extend the budget and execute the step");
        next.Plan[0].State.Should().Be(GoalStepState.Completed);
    }

    [Fact]
    public async Task ExecuteNextAsync_BudgetExhausted_SkipCurrentStep_MarksStepSkippedAndAdvancesCursor()
    {
        var store = new JsonGoalRunStore(Path.Combine(_root, "runs-skip"));
        var step1 = CreateStep(1);
        var step2 = CreateStep(2);
        var run = await CreateClaimedRunAsync(store, [step1, step2], totalAttempts: 5);
        var channel = Channel.CreateUnbounded<TuiEvent>();
        var runtime = CreateRuntimeWithPrompt(store, channel.Writer, maxAttempts: 5);
        await runtime.BindAsync(run, 7, TestContext.Current.CancellationToken);
        var state = new GoalWorkflowState(run.Id, [step1, step2], [], run.Budget, 0, false, GoalRunState.Executing);

        var responder = Task.Run(async () =>
        {
            await foreach (var evt in channel.Reader.ReadAllAsync(TestContext.Current.CancellationToken))
            {
                if (evt is TuiGoalBudgetExhaustedRequest req)
                {
                    req.ResponseSource.TrySetResult(GoalBudgetDecision.SkipCurrentStep);
                    break;
                }
            }
        }, TestContext.Current.CancellationToken);

        var next = await runtime.ExecuteNextAsync(state, TestContext.Current.CancellationToken);
        await responder;

        next.CurrentIndex.Should().Be(1, "skipping the current step must advance the cursor to the next sub-goal");
        next.Plan[0].State.Should().Be(GoalStepState.Skipped);
    }

    [Fact]
    public async Task ExecuteNextAsync_BudgetExhausted_Abort_PersistsCancelledTerminalState()
    {
        var store = new JsonGoalRunStore(Path.Combine(_root, "runs-abort"));
        var step = CreateStep(1);
        var run = await CreateClaimedRunAsync(store, [step], totalAttempts: 5);
        var channel = Channel.CreateUnbounded<TuiEvent>();
        var runtime = CreateRuntimeWithPrompt(store, channel.Writer, maxAttempts: 5);
        await runtime.BindAsync(run, 7, TestContext.Current.CancellationToken);
        var state = new GoalWorkflowState(run.Id, [step], [], run.Budget, 0, false, GoalRunState.Executing);

        var responder = Task.Run(async () =>
        {
            await foreach (var evt in channel.Reader.ReadAllAsync(TestContext.Current.CancellationToken))
            {
                if (evt is TuiGoalBudgetExhaustedRequest req)
                {
                    req.ResponseSource.TrySetResult(GoalBudgetDecision.Abort);
                    break;
                }
            }
        }, TestContext.Current.CancellationToken);

        var next = await runtime.ExecuteNextAsync(state, TestContext.Current.CancellationToken);
        await responder;

        next.State.Should().Be(GoalRunState.Cancelled);
        var persisted = await store.LoadByIdAsync(run.Id, TestContext.Current.CancellationToken);
        persisted!.State.Should().Be(GoalRunState.Cancelled);
        persisted.TerminalReason.Should().Be(RunTerminalReason.Cancelled);
    }

    private GoalWorkflowRuntime CreateRuntimeWithPrompt(
        IGoalRunStore store,
        ChannelWriter<TuiEvent> writer,
        int maxAttempts)
    {
        var stepExecutor = new FakeStepExecutionService();
        var workspace = Substitute.For<IGoalWorkspaceService>();
        workspace.RecordStepAsync(
                Arg.Any<GoalRun>(),
                Arg.Any<GoalStepExecutionEvidence>(),
                Arg.Any<long>(),
                Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var ev = ci.Arg<GoalStepExecutionEvidence>();
                return new GoalStepReceipt("op", ev.GoalId, "commit", ev, false, DateTimeOffset.UtcNow);
            });

        var context = new GoalWorkflowRuntimeContext(
            new GoalRunOptions
            {
                Goal = "test",
                WorkingDirectory = _root,
                Tools = new List<AITool>(),
                Budget = new GoalBudget { MaxSubGoalAttempts = maxAttempts },
            },
            writer,
            static () => new EditTransaction(),
            PromptOnBudgetExhausted: true);

        return new GoalWorkflowRuntime(
            new FakePlanningService(),
            stepExecutor,
            store,
            workspace,
            new FakeCompletionService(),
            context);
    }

    private sealed class FakeCompletionService : IGoalCompletionService
    {
        public Task<GoalRun> CompleteAsync(GoalRun run, long fencingToken, CancellationToken ct)
            => Task.FromResult(run);
    }

    private sealed class FakePlanningService : IGoalPlanningService
    {
        public Task<(GoalPlan Plan, long InputTokens, long OutputTokens, string? Error, bool UsedFallback)>
            DecomposeWithFallbackAsync(string goal, string? modelId, CancellationToken ct)
            => Task.FromResult((new GoalPlan { Goals = [] }, 0L, 0L, (string?)null, false));

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

    private sealed class FakeStepExecutionService(Action<EditTransaction>? onExecute = null) : IGoalStepExecutionService
    {
        public Task<SubGoalExecution> ExecuteSubGoalWithLoopStreamingAsync(
            GoalItem goal,
            GoalRunOptions options,
            EditTransaction sharedTransaction,
            ChannelWriter<TuiEvent> eventWriter,
            CancellationToken ct)
        {
            onExecute?.Invoke(sharedTransaction);
            return Task.FromResult(new SubGoalExecution(goal.Id, GoalStatus.Completed, 1, 10, 5, "ok", "passed"));
        }

        public void UpdateGoalContext(
            GoalPlan plan,
            GoalItem currentGoal,
            IReadOnlyList<SubGoalExecution> executions,
            bool sharedTransactionOwned)
        {
        }
    }

    private async Task<GoalRun> CreateClaimedRunAsync(
        IGoalRunStore store,
        IReadOnlyList<GoalStepSnapshot> plan,
        int totalAttempts)
    {
        var worktreeDir = Path.Combine(_root, "worktree");
        Directory.CreateDirectory(worktreeDir);
        var initial = new GoalRun
        {
            Id = GoalRunId.New(),
            SessionId = SessionId.NewId(),
            Goal = "test",
            WorkingDirectory = _root,
            WorkspaceFingerprint = "fp",
            DefinitionHash = "hash",
            State = GoalRunState.Executing,
            Plan = plan,
            Budget = new GoalBudgetSnapshot(totalAttempts, 100, 50, DateTimeOffset.UtcNow),
            Workspace = new GoalWorkspaceSnapshot(
                "ws",
                _root,
                worktreeDir,
                "onecode/goal/test",
                "main",
                "base",
                "fp",
                DateTimeOffset.UtcNow),
        };
        await store.SaveAsync(initial, 0, TestContext.Current.CancellationToken);
        var saved = await store.LoadByIdAsync(initial.Id, TestContext.Current.CancellationToken)
            ?? throw new InvalidOperationException();
        return await store.ClaimWorkflowAsync(
            initial.Id,
            7,
            saved.Version,
            TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task BuildSubGoalRunOptions_PreservesSessionSystemPromptAlongsideHarness()
    {
        var promptManager = Substitute.For<OneCode.Core.Prompt.IPromptManager>();
        promptManager.GetPromptAsync(PromptComposer.HarnessPromptName, Arg.Any<CancellationToken>())
            .Returns("Harness instructions");
        var goal = new GoalItem
        {
            Id = 1,
            Description = "Collect news and build HTML",
            SuccessCriteria = "index.html exists",
        };
        var options = new GoalRunOptions
        {
            Goal = "Build timeline",
            WorkingDirectory = _root,
            SystemPrompt = "You are OneCode.\n# Environment\n- Current Date: 2026-10-16",
            ModelId = "step-3.5-flash",
            Tools = [AIFunctionFactory.Create(() => "ok", "Read")],
        };
        using var tx = new EditTransaction();

        var runOptions = await GoalSubGoalExecutor.BuildSubGoalRunOptionsAsync(
            promptManager,
            goal,
            options,
            tx,
            "Execute sub-goal 1",
            static _ => { },
            TestContext.Current.CancellationToken);
        var cacheSafe = GoalSubTaskDagRunner.BuildCacheSafeParams(options);

        runOptions.SystemPrompt.Should().Be(options.SystemPrompt);
        runOptions.HarnessInstructions.Should().Be("Harness instructions");
        cacheSafe.SystemPrompt.Should().Be(options.SystemPrompt);
    }

    private static GoalStepSnapshot CreateStep(int id) => new(
        id,
        $"Step {id}",
        "Criteria",
        GoalStepState.Pending,
        ["*"],
        0,
        false,
        [],
        false,
        false,
        false);
}
