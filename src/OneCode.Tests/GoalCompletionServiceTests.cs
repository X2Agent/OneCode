using OneCode.App.Services.Agent;
using OneCode.App.Services.GoalMode;
using OneCode.App.Services.Lsp;
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

    [Fact]
    public async Task Complete_BudgetSkippedRequiredStep_PausesInsteadOfFails()
    {
        // Fix-1/F-03：预算耗尽跳过的必需步骤 → 终态 Paused（可续跑），而非 Failed。
        var store = new JsonGoalRunStore(Path.Combine(_root, "budget-pause"));
        var skippedStep = new GoalStepSnapshot(
            1, "step", "done", GoalStepState.Skipped, [], 0, false, [], [], false, false, false);
        var skippedEvidence = new GoalStepExecutionEvidence(
            1,
            GoalStepState.Skipped,
            0,
            0,
            0,
            string.Empty,
            "Skipped because the remaining attempt budget cannot safely execute another sub-goal.",
            [],
            [],
            [new GoalGateEvidence("budget", false, true, "Insufficient remaining attempt budget.")],
            []);
        var run = await CreateClaimedRunAsync(
            store,
            GoalRunState.Executing,
            stepOverride: skippedStep,
            evidenceOverride: skippedEvidence);
        var service = new GoalCompletionService(
            store,
            new CompletionWorkspaceService(),
            new CompletionStepService(semanticPassed: true));

        var paused = await service.CompleteAsync(run, 7, TestContext.Current.CancellationToken);

        paused.State.Should().Be(GoalRunState.Paused);
        paused.TerminalReason.Should().Be(BuildTerminalReason.BudgetExceeded);
        paused.FinalValidation.Should().Contain(gate =>
            gate.Gate == "state-integrity" && !gate.Passed && gate.Skipped);
    }

    [Fact]
    public async Task Complete_StableDiagnosticError_FailsStaticDiagnosticsGate()
    {
        // Fix-3：诊断稳定后存在 Error → gate 正常判负（不因 quiescent 等待而漏杀）。
        var store = new JsonGoalRunStore(Path.Combine(_root, "diag-error"));
        var changedFile = Path.Combine(_root, "code.cs");
        var evidence = new GoalStepExecutionEvidence(
            1, GoalStepState.Completed, 1, 10, 5, "done", "accepted",
            [changedFile], [],
            [new GoalGateEvidence("test", true, false, "passed")], []);
        var run = await CreateClaimedRunAsync(
            store,
            GoalRunState.Validating,
            evidenceOverride: evidence);
        var registry = new LspDiagnosticRegistry();
        PushError(registry, changedFile, "CS1002: ; expected");
        var service = new GoalCompletionService(
            store,
            new CompletionWorkspaceService(),
            new CompletionStepService(semanticPassed: true),
            diagnosticRegistry: registry,
            diagnosticQuietPeriod: TimeSpan.FromMilliseconds(50),
            diagnosticTimeout: TimeSpan.FromSeconds(5));

        var failed = await service.CompleteAsync(run, 7, TestContext.Current.CancellationToken);

        failed.State.Should().Be(GoalRunState.Failed);
        failed.FinalValidation.Should().Contain(gate =>
            gate.Gate == "static-diagnostics" && !gate.Passed && !gate.Skipped);
    }

    [Fact]
    public async Task Complete_UnstableDiagnostics_SkipsGateInsteadOfFalseVerdict()
    {
        // Fix-3/F-08：诊断持续推送未稳定 → 超时后 gate 标记 Skipped，既不误杀也不假通过。
        var store = new JsonGoalRunStore(Path.Combine(_root, "diag-unstable"));
        var changedFile = Path.Combine(_root, "unstable.cs");
        var evidence = new GoalStepExecutionEvidence(
            1, GoalStepState.Completed, 1, 10, 5, "done", "accepted",
            [changedFile], [],
            [new GoalGateEvidence("test", true, false, "passed")], []);
        var run = await CreateClaimedRunAsync(
            store,
            GoalRunState.Validating,
            evidenceOverride: evidence);
        var registry = new LspDiagnosticRegistry();
        using var source = new CancellationTokenSource();
        var chatter = Task.Run(async () =>
        {
            var index = 0;
            while (!source.IsCancellationRequested)
            {
                PushError(registry, changedFile, $"error-{index++}");
                await Task.Delay(20, source.Token);
            }
        });
        try
        {
            var service = new GoalCompletionService(
                store,
                new CompletionWorkspaceService(),
                new CompletionStepService(semanticPassed: true),
                diagnosticRegistry: registry,
                diagnosticQuietPeriod: TimeSpan.FromMilliseconds(60),
                diagnosticTimeout: TimeSpan.FromMilliseconds(300));

            var completed = await service.CompleteAsync(run, 7, TestContext.Current.CancellationToken);

            completed.FinalValidation.Should().Contain(gate =>
                gate.Gate == "static-diagnostics" && gate.Skipped);
        }
        finally
        {
            source.Cancel();
            try { await chatter; } catch (OperationCanceledException) { }
        }
    }

    private static void PushError(LspDiagnosticRegistry registry, string filePath, string message)
    {
        var payload = System.Text.Json.JsonSerializer.SerializeToElement(new
        {
            uri = new Uri(filePath).AbsoluteUri,
            diagnostics = new object[] { new { severity = 1, message } },
        });
        registry.ProcessDiagnostics("test-server", payload);
    }

    private async Task<GoalRun> CreateClaimedRunAsync(
        IGoalRunStore store,
        GoalRunState state,
        bool validEvidence = true,
        GoalStepSnapshot? stepOverride = null,
        GoalStepExecutionEvidence? evidenceOverride = null)
    {
        var step = stepOverride ?? new GoalStepSnapshot(
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
        var evidence = evidenceOverride ?? new GoalStepExecutionEvidence(
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
