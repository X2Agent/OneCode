using System.Threading.Channels;
using Microsoft.Extensions.AI;
using NSubstitute;
using OneCode.App.Services.Agent;
using OneCode.App.Services.GoalMode;
using OneCode.App.Query;
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
        persisted.TerminalReason.Should().Be(RunTerminalReason.AgentException);
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
    public async Task FailedStep_Replan_MarksFailedStepSupersededAndAppendsReplacement()
    {
        // 回归锚定：replan 后失败步必须标记 Skipped + SupersededByReplan（保留在 plan 中——
        // 聚合不变式要求回执引用的步骤存在于 plan），否则完成完整性检查永远不满足，
        // replan 后的 run 终态必为 Failed，replan 形同虚设。游标指向首个 replanned 步。
        var store = new JsonGoalRunStore(Path.Combine(_root, "replan-runs"));
        var run = await CreateClaimedRunAsync(store, [Snapshot(1), Snapshot(2)], GoalRunState.Executing);
        var failedExecution = new SubGoalExecution(
            1,
            GoalStatus.Failed,
            1,
            5,
            2,
            "partial",
            "acceptance not met",
            null);
        var planning = new FakePlanningService
        {
            ReplanResult = [new GoalItem { Id = 2, Description = "replanned step", SuccessCriteria = "done" }],
        };
        var steps = new FakeStepExecutionService(failedExecution);
        var workspace = new FakeWorkspaceService();
        var (runtime, events) = CreateRuntimeWithEvents(store, planning, steps, workspace);
        await runtime.BindAsync(run, 7, TestContext.Current.CancellationToken);
        var state = new GoalWorkflowState(run.Id, [Snapshot(1), Snapshot(2)], [], run.Budget, 0, false, GoalRunState.Executing);

        var applied = await runtime.ExecuteNextAsync(state, TestContext.Current.CancellationToken);

        planning.ReplanCalls.Should().Be(1);
        applied.HasReplanned.Should().BeTrue();
        applied.CurrentIndex.Should().Be(1, "游标指向首个 replanned 步骤（失败步之后的替代步骤）");
        applied.Plan.Should().HaveCount(2, "失败步（标记后）保留 + replanned 步骤");
        var failedStep = applied.Plan.Single(step => step.Id == 1);
        failedStep.State.Should().Be(GoalStepState.Skipped);
        failedStep.SupersededByReplan.Should().BeTrue();
        applied.Plan.Should().NotContain(step => step.State == GoalStepState.Failed,
            "失败态不得留在 plan，否则完成完整性检查永远不满足");
        applied.Plan.Last().Id.Should().Be(2, "replanned 步骤紧接保留步骤连续编号为 #2");

        var published = new List<QueryEvent>();
        while (events.Reader.TryRead(out var evt))
            published.Add(evt);
        var lastPlanUpdate = published.OfType<GoalPlanUpdatedEvent>().Last();
        lastPlanUpdate.Steps.Select(s => s.Id).Should().Equal(new[] { 1, 2 },
            "重规划完成后必须立即向 TUI 推送更新后的计划快照，无需等待下一步开始");
        lastPlanUpdate.Steps[0].SupersededByReplan.Should().BeTrue();

        var persisted = await store.LoadByIdAsync(run.Id, TestContext.Current.CancellationToken);
        persisted!.Executions.Should().Contain(e => e.GoalId == 1 && e.State == GoalStepState.Failed,
            "失败步证据保留在 Executions 供审计");
    }

    [Fact]
    public async Task ExecuteNext_FirstStep_AttemptBudgetInsufficient_PausesWithoutSkipReceipt()
    {
        // Fix-7/N-01：守卫不依赖 CurrentIndex > 0——resume 到 index 0 时同样受剩余额度约束。
        // B3 修订：额度不足改为 Paused（不再产出 Skipped 回执）——跳过回执会让该步在
        // resume 后永不重执行，「追加预算 + /resume 续跑」承诺失效。
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
        workspace.RecordCalls.Should().Be(0, "不得留下 Skipped 回执——它会让该步在续跑时永不重执行");
        result.State.Should().Be(GoalRunState.Paused);
        result.CurrentIndex.Should().Be(0, "游标保留，追加预算续跑后从当前步开始");
        var persisted = await store.LoadByIdAsync(run.Id, TestContext.Current.CancellationToken);
        persisted!.State.Should().Be(GoalRunState.Paused);
        persisted.Plan.Should().ContainSingle().Which.State.Should().Be(GoalStepState.Pending);
        persisted.TerminalReason.Should().Be(RunTerminalReason.BudgetExceeded);
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
            new GoalBudgetSnapshot(3, 30, 15, DateTimeOffset.UtcNow));
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
        var startTime = DateTimeOffset.UtcNow;
        var store = new JsonGoalRunStore(Path.Combine(_root, "warning-events"));
        var plan = new[] { Snapshot(1), Snapshot(2) };
        // 14/20 = 70% → Early（黄色）。
        var run = await CreateClaimedRunWithBudgetAsync(
            store,
            plan,
            new GoalBudgetSnapshot(14, 0, 0, DateTimeOffset.UtcNow));
        var (runtime, events) = CreateRuntimeWithEvents(
            store,
            new FakePlanningService(),
            new FakeStepExecutionService(CompletedExecution(1)),
            new FakeWorkspaceService());
        await runtime.BindAsync(run, 7, TestContext.Current.CancellationToken);
        var state = new GoalWorkflowState(run.Id, plan, [], run.Budget, 0, false, GoalRunState.Executing);

        var first = await runtime.ExecuteNextAsync(state, TestContext.Current.CancellationToken);
        await runtime.ExecuteNextAsync(first, TestContext.Current.CancellationToken);

        var published = new List<QueryEvent>();
        while (events.Reader.TryRead(out var evt))
            published.Add(evt);
        published.OfType<GoalBudgetWarningEvent>().Should().ContainSingle()
            .Which.Level.Should().Be(GoalBudgetWarningLevel.Early);

        var persisted = await store.LoadByIdAsync(run.Id, TestContext.Current.CancellationToken);
        persisted!.Budget.LastActivityAt.Should().NotBeNull("wall clock tracking must stamp activity");
        persisted.Budget.LastActivityAt!.Value.Should().BeOnOrAfter(startTime)
            .And.BeOnOrBefore(DateTimeOffset.UtcNow.AddSeconds(5), "LastActivityAt 必须落在测试执行时间窗内");
    }

    [Fact]
    public async Task PlanAsync_PublishesPlanListAndProgress()
    {
        // P1：分解完成后必须发布 GoalPlanEvent（编号清单）+ 开始执行进度，消除规划期静默。
        var store = new JsonGoalRunStore(Path.Combine(_root, "plan-events"));
        var run = await CreateClaimedRunAsync(store);
        var (runtime, events) = CreateRuntimeWithEvents(
            store,
            new FakePlanningService(),
            new FakeStepExecutionService(CompletedExecution(1)),
            new FakeWorkspaceService());
        await runtime.BindAsync(run, 7, TestContext.Current.CancellationToken);

        await runtime.PlanAsync(
            new GoalWorkflowInput(run.Id, run.Goal, "model", "tools"),
            TestContext.Current.CancellationToken);

        var published = new List<QueryEvent>();
        while (events.Reader.TryRead(out var evt))
            published.Add(evt);
        published.OfType<GoalPlanEvent>().Should().ContainSingle()
            .Which.Steps.Should().ContainSingle().Which.Should().NotBeNullOrEmpty();
        published.OfType<ModeProgressEvent>().Should()
            .Contain(p => p.Message.Contains("已分解为"));
    }

    [Fact]
    public async Task ExecuteNext_PublishesStepStartAndReceiptProgress()
    {
        // P0/P3：步骤开始（含预算快照）与步骤回执（✓/✗ + 轮数 + 文件数）必须发布。
        var store = new JsonGoalRunStore(Path.Combine(_root, "step-events"));
        var plan = new[] { Snapshot(1), Snapshot(2) };
        var run = await CreateClaimedRunAsync(store, plan, GoalRunState.Executing);
        var (runtime, events) = CreateRuntimeWithEvents(
            store,
            new FakePlanningService(),
            new FakeStepExecutionService(CompletedExecution(1)),
            new FakeWorkspaceService());
        await runtime.BindAsync(run, 7, TestContext.Current.CancellationToken);
        var state = new GoalWorkflowState(run.Id, plan, [], run.Budget, 0, false, GoalRunState.Executing);

        await runtime.ExecuteNextAsync(state, TestContext.Current.CancellationToken);

        var published = new List<QueryEvent>();
        while (events.Reader.TryRead(out var evt))
            published.Add(evt);
        published.OfType<ModeProgressEvent>().Should().Contain(p => p.Message.Contains("子目标 1/2"));
        published.OfType<ModeProgressEvent>().Should().Contain(p => p.Message.Contains("✓ 子目标 1/2 完成"));
    }

    private GoalWorkflowRuntime CreateRuntime(
        IGoalRunStore store,
        IGoalPlanningService planning,
        IGoalStepExecutionService steps,
        IGoalWorkspaceService workspace)
    {
        return new GoalWorkflowRuntime(
            planning,
            steps,
            store,
            workspace,
            new FakeCompletionService(store),
            new GoalWorkflowRuntimeContext(
                new GoalRunOptions
                {
                    Goal = "goal",
                    WorkingDirectory = _root,
                    ModelId = "model",
                    Tools = new List<AITool>(),
                },
                Channel.CreateUnbounded<QueryEvent>().Writer,
                static () => new EditTransaction()),
            Substitute.For<IBudgetDecisionService>());
    }

    private (GoalWorkflowRuntime Runtime, Channel<QueryEvent> Events) CreateRuntimeWithEvents(
        IGoalRunStore store,
        IGoalPlanningService planning,
        IGoalStepExecutionService steps,
        IGoalWorkspaceService workspace,
        GoalBudget? budget = null)
    {
        var events = Channel.CreateUnbounded<QueryEvent>();
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
            new GoalWorkflowRuntimeContext(
                options,
                events.Writer,
                static () => new EditTransaction()),
            Substitute.For<IBudgetDecisionService>());
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
        public int ReplanCalls { get; private set; }
        public bool UsedFallback { get; set; }
        public string? Error { get; set; }

        /// <summary>非空时 ReplanAsync 返回该列表（调用方已按 Max+1 规则编号，与真实分解器一致）。</summary>
        public List<GoalItem>? ReplanResult { get; set; }

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
        {
            ReplanCalls++;
            if (ReplanResult is null)
                return Task.FromResult<(List<GoalItem>, long, long)?>(null);
            return Task.FromResult<(List<GoalItem>, long, long)?>((ReplanResult, 1L, 1L));
        }

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
            ChannelWriter<QueryEvent> eventWriter,
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
            IReadOnlyList<SubGoalExecution> executions)
        {
        }
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
        AllowedPaths = item.AllowedPaths,
        RequiresBuild = item.RequiresBuild,
        RequiresTests = item.RequiresTests,
        Optional = item.Optional,
    };
}
