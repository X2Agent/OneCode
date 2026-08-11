using Microsoft.Extensions.Logging;
using NSubstitute;
using OneCode.App.Services.Agent;
using OneCode.App.Services.BuildMode;
using OneCode.Core.Build;
using OneCode.Core.Domain;
using OneCode.Core.Tasks;
using OneCode.Infrastructure.Build;
using TaskStatus = OneCode.Core.Tasks.TaskStatus;

namespace OneCode.Tests;

public sealed class BuildRunCoordinatorTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "OneCodeBuildRunTests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void RequirementAssessment_BroadProductRequest_RequiresClarification()
    {
        var sut = new RequirementAssessmentService();

        var result = sut.Assess("Build a product-development and AI system.");

        result.RequiresClarification.Should().BeTrue();
        sut.BuildClarificationQuestions(result).Should().NotBeEmpty();
    }

    [Fact]
    public void RequirementAssessment_ClarificationQuestions_IncludeRequestContext()
    {
        var sut = new RequirementAssessmentService();
        const string prompt = "开发一个订单和支付平台";
        var assessment = sut.Assess(prompt);

        var questions = sut.BuildClarificationQuestions(assessment, prompt);

        questions.Should().NotBeEmpty();
        questions.Should().OnlyContain(question => question.Contains(prompt));
    }

    [Fact]
    public void RequirementAssessment_ExplicitFixAndTest_DoesNotRequireClarification()
    {
        var sut = new RequirementAssessmentService();

        var result = sut.Assess("Fix the null reference in Foo.cs line 42 and run FooTests.");

        result.RequiresClarification.Should().BeFalse();
    }

    [Fact]
    public void RequirementAssessment_ModuleRefactorWithoutAcceptance_RequiresClarification()
    {
        var sut = new RequirementAssessmentService();

        var result = sut.Assess("重构认证模块");

        result.GoalIsClear.Should().BeTrue();
        result.ScopeIsBounded.Should().BeTrue();
        result.AcceptanceIsDeterministic.Should().BeFalse();
        result.Risk.Should().Be(BuildRiskLevel.High);
        result.RequiresClarification.Should().BeTrue();
    }

    [Fact]
    public void RequirementAssessment_BoundedRefactorWithInvariantAndTests_DoesNotRequireClarification()
    {
        var sut = new RequirementAssessmentService();

        var result = sut.Assess("重构 AuthenticationService，保持公共 API 兼容，并运行认证测试验证行为不变。");

        result.GoalIsClear.Should().BeTrue();
        result.ScopeIsBounded.Should().BeTrue();
        result.AcceptanceIsDeterministic.Should().BeTrue();
        result.ConstraintsAreComplete.Should().BeTrue();
        result.RequiresUserDecision.Should().BeFalse();
        result.RequiresClarification.Should().BeFalse();
    }

    [Fact]
    public void RequirementAssessment_MultipleDomainsAndExternalDependencies_RequiresClarification()
    {
        var sut = new RequirementAssessmentService();

        var result = sut.Assess("开发一个需求、研发、测试、发布全流程平台，接入数据库、消息队列和云部署。");

        result.ScopeIsBounded.Should().BeFalse();
        result.ConstraintsAreComplete.Should().BeFalse();
        result.RequiresUserDecision.Should().BeTrue();
        result.Risk.Should().Be(BuildRiskLevel.Medium);
        result.RequiresClarification.Should().BeTrue();
    }

    [Fact]
    public void Transition_ToCompleted_RejectsMissingValidationEvidence()
    {
        var sut = new BuildStateTransitionService();
        var run = CreateAcceptingRun() with { Validations = [] };

        var act = () => sut.Transition(run, BuildRunState.Completed, DateTimeOffset.UtcNow);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*final validation*");
    }

    [Fact]
    public void Transition_ToCompleted_RejectsTaskWithoutEvidence()
    {
        var sut = new BuildStateTransitionService();
        var run = CreateAcceptingRun() with
        {
            Plan = CreateAcceptingRun().Plan! with
            {
                Tasks = [new BuildPlanTask(
                    "t1",
                    "Fix",
                    "Fix",
                    [],
                    ["Foo.cs"],
                    ["a1"],
                    BuildTaskStatus.Completed)],
            },
        };

        var act = () => sut.Transition(run, BuildRunState.Completed, DateTimeOffset.UtcNow);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*evidence*");
    }

    [Fact]
    public void Transition_ToCompleted_RejectsEvidenceWithoutPersistentTaskCompletion()
    {
        var sut = new BuildStateTransitionService();
        var run = CreateAcceptingRun() with
        {
            Plan = CreateAcceptingRun().Plan! with
            {
                Tasks = [new BuildPlanTask(
                    "t1",
                    "Fix",
                    "Fix",
                    [],
                    ["Foo.cs"],
                    ["a1"],
                    BuildTaskStatus.Completed,
                    [new BuildTaskEvidence(BuildEvidenceKind.Validation, "v1", "Tests passed.")],
                    TaskItemId: "task-1")],
            },
        };

        var act = () => sut.Transition(run, BuildRunState.Completed, DateTimeOffset.UtcNow);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*TaskCompletion*");
    }

    [Fact]
    public void Transition_ToCompleted_RejectsSkippedValidation()
    {
        var sut = new BuildStateTransitionService();
        var run = CreateAcceptingRun() with
        {
            Validations = [new BuildValidationRun(
                "v1",
                BuildValidationStatus.Skipped,
                [],
                ["No profile."],
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow)],
        };

        var act = () => sut.Transition(run, BuildRunState.Completed, DateTimeOffset.UtcNow);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*final validation*");
    }

    [Fact]
    public async Task BeginOrResumeAsync_AmbiguousRequest_PersistsClarifyingRun()
    {
        var store = new InMemoryBuildRunStore();
        var sut = CreateCoordinator(store, "fingerprint-a");
        var sessionId = SessionId.NewId();

        var run = await sut.BeginOrResumeAsync(
            sessionId,
            "Build a product-development and AI system.",
            Path.GetTempPath());

        run.State.Should().Be(BuildRunState.Clarifying);
        run.Scope.Should().BeNull();
        run.ClarificationQuestions.Should().NotBeEmpty();
        (await store.LoadAsync(sessionId)).Should().BeEquivalentTo(run);
    }

    [Fact]
    public async Task BeginOrResumeAsync_ExplicitRequest_ParksAtPlannedUntilApproved()
    {
        var store = new InMemoryBuildRunStore();
        var sut = CreateCoordinator(store, "fingerprint-a");

        var run = await sut.BeginOrResumeAsync(
            SessionId.NewId(),
            "Fix Foo.cs line 42 and run FooTests.",
            Path.GetTempPath());

        run.State.Should().Be(BuildRunState.Planned);
        run = await ApprovePlanAsync(sut, run);
        run.State.Should().Be(BuildRunState.Implementing);
        run.Scope.Should().NotBeNull();
        run.Plan.Should().NotBeNull();
        run.TerminalReason.Should().BeNull();
        run.ApprovedToolPolicy.Should().NotBeNull();
        run.PlanApprovedAt.Should().NotBeNull();
        run.PlanApprovalSource.Should().Be("test");
    }

    [Fact]
    public async Task BeginOrResumeAsync_PersistsObservablePlanningSequence()
    {
        var store = new JsonBuildRunStore(_root);
        var provider = Substitute.For<IWorkspaceFingerprintProvider>();
        provider.ComputeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult("fingerprint-a"));
        var sut = CreateCoordinator(store, provider);
        var observed = new List<BuildRunState>();

        var run = await sut.BeginOrResumeAsync(
            SessionId.NewId(),
            "Fix Foo.cs line 42 and run FooTests.",
            Path.GetTempPath(),
            TestContext.Current.CancellationToken,
            state => observed.Add(state.State));
        var events = await store.LoadEventsAsync(
            run.Id,
            TestContext.Current.CancellationToken);

        run.State.Should().Be(BuildRunState.Planned);
        run = await ApprovePlanAsync(sut, run);
        observed.Should().ContainInOrder(
            BuildRunState.Intake,
            BuildRunState.Assessing,
            BuildRunState.ScopeConfirmed,
            BuildRunState.Planning,
            BuildRunState.Planned);
        events.Select(item => item.ToState).Should().ContainInOrder(
            BuildRunState.Intake,
            BuildRunState.Assessing,
            BuildRunState.ScopeConfirmed,
            BuildRunState.Planning,
            BuildRunState.Planned);
    }

    [Fact]
    public async Task PrepareAttemptAsync_VerifyingRun_ResetsEphemeralEvidenceAndRejectsStaleToken()
    {
        var store = new InMemoryBuildRunStore();
        var sut = CreateCoordinator(store, "fingerprint-a");
        var run = await sut.BeginOrResumeAsync(
            SessionId.NewId(),
            "Fix Foo.cs line 42 and run FooTests.",
            Path.GetTempPath(),
            TestContext.Current.CancellationToken);
        run = await ApprovePlanAsync(sut, run);
        var claimed = await store.ClaimWorkflowAsync(
            run.Id,
            8,
            run.Version,
            TestContext.Current.CancellationToken);
        var verifying = await sut.BeginVerificationAsync(
            claimed.Id,
            TestContext.Current.CancellationToken,
            expectedWorkflowFencingToken: 8);

        var stale = () => sut.PrepareAttemptAsync(
            verifying.Id,
            7,
            TestContext.Current.CancellationToken);
        await stale.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Stale*token*");

        var prepared = await sut.PrepareAttemptAsync(
            verifying.Id,
            8,
            TestContext.Current.CancellationToken);
        prepared.State.Should().Be(BuildRunState.Implementing);
        prepared.WorkflowFencingToken.Should().Be(8);
        prepared.Validations.Should().BeEmpty();
        prepared.ChangedFiles.Should().BeEmpty();
        prepared.ToolBatches.Should().BeEmpty();
    }

    [Fact]
    public async Task BeginVerificationAsync_PersistsVerifyingAndIsIdempotent()
    {
        var store = new InMemoryBuildRunStore();
        var sut = CreateCoordinator(store, "fingerprint-a");
        var run = await sut.BeginOrResumeAsync(
            SessionId.NewId(),
            "Fix Foo.cs line 42 and run FooTests.",
            Path.GetTempPath(),
            TestContext.Current.CancellationToken);
        run = await ApprovePlanAsync(sut, run);

        var first = await sut.BeginVerificationAsync(
            run.Id,
            TestContext.Current.CancellationToken);
        var repeated = await sut.BeginVerificationAsync(
            run.Id,
            TestContext.Current.CancellationToken);

        first.State.Should().Be(BuildRunState.Verifying);
        repeated.Should().BeEquivalentTo(first);
        repeated.Version.Should().Be(first.Version);
    }

    [Theory]
    [InlineData(BuildRunState.Intake)]
    [InlineData(BuildRunState.Assessing)]
    public async Task BeginOrResumeAsync_PreExecutionCheckpoint_ContinuesDeterministically(
        BuildRunState checkpointState)
    {
        var store = new InMemoryBuildRunStore();
        var sut = CreateCoordinator(store, "fingerprint-a");
        var now = DateTimeOffset.UtcNow;
        var conversationId = SessionId.NewId();
        var run = new BuildRun
        {
            Id = BuildRunId.New(),
            ConversationId = conversationId,
            State = checkpointState,
            IntakePrompt = "Fix Foo.cs line 42 and run FooTests.",
            WorkingDirectory = Path.GetTempPath(),
            WorkspaceFingerprint = "fingerprint-a",
            SequenceNumber = checkpointState == BuildRunState.Intake ? 1 : 2,
            CreatedAt = now,
            UpdatedAt = now,
        };
        await store.SaveAsync(run, 0, TestContext.Current.CancellationToken);

        var resumed = await sut.BeginOrResumeAsync(
            conversationId,
            "continue",
            Path.GetTempPath(),
            TestContext.Current.CancellationToken);

        resumed.State.Should().Be(BuildRunState.Planned);
        resumed = await ApprovePlanAsync(sut, resumed);
        resumed.State.Should().Be(BuildRunState.Implementing);
        resumed.Scope.Should().NotBeNull();
        resumed.Plan.Should().NotBeNull();
    }

    [Fact]
    public async Task BeginOrResumeAsync_PlanningCheckpoint_DoesNotDuplicatePersistentTask()
    {
        var store = new InMemoryBuildRunStore();
        var tasks = new TaskService();
        var sut = CreateCoordinator(store, "fingerprint-a", tasks);
        var now = DateTimeOffset.UtcNow;
        var conversationId = SessionId.NewId();
        var run = new BuildRun
        {
            Id = BuildRunId.New(),
            ConversationId = conversationId,
            State = BuildRunState.Planning,
            IntakePrompt = "Fix Foo.cs line 42 and run FooTests.",
            Scope = new BuildScopeSnapshot(
                "Fix Foo.cs",
                ["Foo.cs"],
                [],
                [],
                [new AcceptanceCriterion("a1", "Tests pass", true)],
                "user",
                now),
            WorkingDirectory = Path.GetTempPath(),
            WorkspaceFingerprint = "fingerprint-a",
            CreatedAt = now,
            UpdatedAt = now,
        };
        await store.SaveAsync(run, 0, TestContext.Current.CancellationToken);

        var resumed = await sut.BeginOrResumeAsync(
            conversationId,
            "continue",
            Path.GetTempPath(),
            TestContext.Current.CancellationToken);
        var scopedTasks = tasks.ListTasks(
            conversationId: conversationId.ToString(),
            buildRunId: run.Id.ToString(),
            exactScope: true);

        resumed.State.Should().Be(BuildRunState.Planned);
        scopedTasks.Should().ContainSingle();
        resumed.Plan!.Tasks.Should().ContainSingle(task => task.TaskItemId == scopedTasks[0].Id);
    }

    [Fact]
    public async Task BeginOrResumeAsync_WorkspaceChanged_BlocksUnfinishedRun()
    {
        var store = new InMemoryBuildRunStore();
        var sessionId = SessionId.NewId();
        var first = CreateCoordinator(store, "fingerprint-a");
        await first.BeginOrResumeAsync(
            sessionId,
            "Fix Foo.cs line 42 and run FooTests.",
            Path.GetTempPath());
        var resumed = CreateCoordinator(store, "fingerprint-b");

        var run = await resumed.BeginOrResumeAsync(
            sessionId,
            "continue",
            Path.GetTempPath());

        run.State.Should().Be(BuildRunState.Blocked);
        run.FailureSummary.Should().Contain("Workspace changed");
    }

    [Fact]
    public async Task CompleteAsync_PassingEvidence_ProducesCompletedRun()
    {
        var store = new InMemoryBuildRunStore();
        var sut = CreateCoordinator(store, "fingerprint-a");
        var run = await sut.BeginOrResumeAsync(
            SessionId.NewId(),
            "Fix Foo.cs line 42 and run FooTests.",
            Path.GetTempPath());
        run = await ApprovePlanAsync(sut, run);

        var accepting = await sut.CompleteAsync(
            run.Id,
            new MainAgentRunResult(
                "done",
                100,
                50,
                3,
                TerminalReason: BuildTerminalReason.Completed,
                FinalValidationStatus: BuildValidationStatus.Passed,
                ModifiedFiles: ["Foo.cs"]));
        var completed = await sut.ConfirmCommitAsync(accepting.Id);

        accepting.State.Should().Be(BuildRunState.Accepting);
        accepting.TransactionCommitted.Should().BeFalse();
        accepting.CommitWorkspaceFingerprint.Should().Be("fingerprint-a");
        completed.State.Should().Be(BuildRunState.Completed);
        completed.TransactionCommitted.Should().BeTrue();
        completed.Plan!.Tasks.Should().OnlyContain(task =>
            task.Status == BuildTaskStatus.Completed && task.CompletionEvidence.Count > 0);
        completed.DeliveryManifest.Should().NotBeNull();
        completed.DeliveryManifest!.ChangedFiles.Should().Contain("Foo.cs");
        completed.Scope!.AcceptanceCriteria.Should().OnlyContain(item =>
            item.Status == AcceptanceStatus.Passed && !string.IsNullOrWhiteSpace(item.Evidence));
    }

    [Fact]
    public async Task BeginOrResumeAsync_MapsBuildPlanTaskToPersistentScopedTask()
    {
        var store = new InMemoryBuildRunStore();
        var tasks = new TaskService();
        var sut = CreateCoordinator(store, "fingerprint-a", tasks);
        var conversationId = SessionId.NewId();

        var run = await sut.BeginOrResumeAsync(
            conversationId,
            "Fix Foo.cs line 42 and run FooTests.",
            Path.GetTempPath());

        var planTask = run.Plan!.Tasks.Should().ContainSingle().Subject;
        planTask.TaskItemId.Should().NotBeNullOrWhiteSpace();
        var taskItem = tasks.GetTask(planTask.TaskItemId!);
        taskItem.Should().NotBeNull();
        taskItem!.ConversationId.Should().Be(conversationId.ToString());
        taskItem.BuildRunId.Should().Be(run.Id.ToString());
        taskItem.Status.Should().Be(TaskStatus.InProgress);
        taskItem.Metadata!.ExtraProperties!["BuildPlanTaskId"].Should().Be(planTask.Id);
    }

    [Fact]
    public async Task CompleteAsync_PersistentTaskCompletionBecomesTaskSpecificEvidence()
    {
        var store = new InMemoryBuildRunStore();
        var tasks = new TaskService();
        var sut = CreateCoordinator(store, "fingerprint-a", tasks);
        var run = await sut.BeginOrResumeAsync(
            SessionId.NewId(),
            "Fix Foo.cs line 42 and run FooTests.",
            Path.GetTempPath());
        run = await ApprovePlanAsync(sut, run);

        var accepting = await sut.CompleteAsync(
            run.Id,
            new MainAgentRunResult(
                "done",
                100,
                50,
                3,
                TerminalReason: BuildTerminalReason.Completed,
                FinalValidationStatus: BuildValidationStatus.Passed,
                ModifiedFiles: ["Foo.cs"]));
        var completed = await sut.ConfirmCommitAsync(accepting.Id);

        var planTask = completed.Plan!.Tasks.Should().ContainSingle().Subject;
        tasks.GetTask(planTask.TaskItemId!)!.Status.Should().Be(TaskStatus.Completed);
        planTask.CompletionEvidence.Should().ContainSingle(evidence =>
            evidence.Kind == BuildEvidenceKind.TaskCompletion
            && evidence.Reference == planTask.TaskItemId);
    }

    [Fact]
    public async Task ConfirmCommitAsync_RepeatedCall_IsIdempotent()
    {
        var store = new InMemoryBuildRunStore();
        var sut = CreateCoordinator(store, "fingerprint-a");
        var run = await sut.BeginOrResumeAsync(
            SessionId.NewId(),
            "Fix Foo.cs line 42 and run FooTests.",
            Path.GetTempPath());
        run = await ApprovePlanAsync(sut, run);
        var accepting = await sut.CompleteAsync(
            run.Id,
            new MainAgentRunResult(
                "done",
                100,
                50,
                3,
                TerminalReason: BuildTerminalReason.Completed,
                FinalValidationStatus: BuildValidationStatus.Passed,
                ModifiedFiles: ["Foo.cs"]));

        var first = await sut.ConfirmCommitAsync(accepting.Id);
        var second = await sut.ConfirmCommitAsync(accepting.Id);

        first.State.Should().Be(BuildRunState.Completed);
        second.Should().BeEquivalentTo(first);
    }

    [Fact]
    public async Task ConfirmCommitAsync_WorkspaceChangedAfterValidation_BlocksRun()
    {
        var store = new InMemoryBuildRunStore();
        var provider = Substitute.For<IWorkspaceFingerprintProvider>();
        provider.ComputeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(
                Task.FromResult("fingerprint-a"),
                Task.FromResult("fingerprint-a"),
                Task.FromResult("fingerprint-a"),
                Task.FromResult("fingerprint-b"));
        var sut = CreateCoordinator(store, provider);
        var run = await sut.BeginOrResumeAsync(
            SessionId.NewId(),
            "Fix Foo.cs line 42 and run FooTests.",
            Path.GetTempPath());
        run = await ApprovePlanAsync(sut, run);
        var accepting = await sut.CompleteAsync(
            run.Id,
            new MainAgentRunResult(
                "done",
                100,
                50,
                3,
                TerminalReason: BuildTerminalReason.Completed,
                FinalValidationStatus: BuildValidationStatus.Passed,
                ModifiedFiles: ["Foo.cs"]));

        var blocked = await sut.ConfirmCommitAsync(accepting.Id);

        blocked.State.Should().Be(BuildRunState.Blocked);
        blocked.TransactionCommitted.Should().BeFalse();
        blocked.FailureSummary.Should().Contain("after final validation");
    }

    [Fact]
    public async Task BeginOrResumeAsync_PlannedCheckpoint_PreservesSnapshotForDurableHost()
    {
        var store = new InMemoryBuildRunStore();
        var tasks = new TaskService();
        var sut = CreateCoordinator(store, "fingerprint-a", tasks);
        var conversationId = SessionId.NewId();
        var original = await sut.BeginOrResumeAsync(
            conversationId,
            "Fix Foo.cs line 42 and run FooTests.",
            Path.GetTempPath());

        var resumed = await sut.BeginOrResumeAsync(
            conversationId,
            "continue",
            Path.GetTempPath());

        resumed.Should().BeEquivalentTo(original);
        resumed.State.Should().Be(BuildRunState.Planned);
        resumed.Plan!.Tasks.Should().OnlyContain(task =>
            task.Status == BuildTaskStatus.InProgress && task.CompletionEvidence.Count == 0);
    }

    [Fact]
    public async Task BeginOrResumeAsync_MultiTaskRecovery_PreservesCompletedAndKeepsBlockedPending()
    {
        var store = new InMemoryBuildRunStore();
        var tasks = new TaskService();
        var sut = CreateCoordinator(store, "fingerprint-a", tasks);
        var conversationId = SessionId.NewId();
        var run = await sut.BeginOrResumeAsync(
            conversationId,
            "Execute approved Foo.cs and FooTests.cs plan.",
            Path.GetTempPath(),
            TestContext.Current.CancellationToken,
            prescribedPlan: CreatePrescribedPlan());
        run = await ApprovePlanAsync(sut, run);
        var implementation = run.Plan!.Tasks.Single(task => task.Id == "implementation");
        tasks.ProjectTaskStatus(
            implementation.TaskItemId!,
            TaskStatus.Completed,
            "implementation evidence",
            "plan:2:implementation").Succeeded.Should().BeTrue();

        run = await store.ClaimWorkflowAsync(
            run.Id,
            5,
            run.Version,
            TestContext.Current.CancellationToken);
        run = await sut.BeginVerificationAsync(
            run.Id,
            TestContext.Current.CancellationToken,
            expectedWorkflowFencingToken: 5);
        var resumed = await sut.PrepareAttemptAsync(
            run.Id,
            5,
            TestContext.Current.CancellationToken);

        resumed.Plan!.Tasks.Single(task => task.Id == "implementation").Status
            .Should().Be(BuildTaskStatus.Completed);
        resumed.Plan.Tasks.Single(task => task.Id == "verification").Status
            .Should().Be(BuildTaskStatus.InProgress);
        tasks.ListTasks(
                conversationId: conversationId.ToString(),
                buildRunId: resumed.Id.ToString(),
                exactScope: true)
            .Should().HaveCount(2);
    }

    [Fact]
    public async Task CompleteAsync_CancelledRun_PersistsCancelledTerminalState()
    {
        var store = new InMemoryBuildRunStore();
        var sut = CreateCoordinator(store, "fingerprint-a");
        var run = await sut.BeginOrResumeAsync(
            SessionId.NewId(),
            "Fix Foo.cs line 42 and run FooTests.",
            Path.GetTempPath());
        run = await ApprovePlanAsync(sut, run);

        var cancelled = await sut.CompleteAsync(
            run.Id,
            new MainAgentRunResult(
                null,
                0,
                0,
                1,
                TerminalReason: BuildTerminalReason.Cancelled,
                TransactionRolledBack: true,
                FinalValidationStatus: BuildValidationStatus.Cancelled));

        cancelled.State.Should().Be(BuildRunState.Cancelled);
        cancelled.TerminalReason.Should().Be(BuildTerminalReason.Cancelled);
        cancelled.TransactionRolledBack.Should().BeTrue();
    }

    [Fact]
    public async Task CompleteAsync_AgentException_PersistsFailedTerminalState()
    {
        var store = new InMemoryBuildRunStore();
        var sut = CreateCoordinator(store, "fingerprint-a");
        var run = await sut.BeginOrResumeAsync(
            SessionId.NewId(),
            "Fix Foo.cs line 42 and run FooTests.",
            Path.GetTempPath());
        run = await ApprovePlanAsync(sut, run);

        var failed = await sut.CompleteAsync(
            run.Id,
            new MainAgentRunResult(
                null,
                0,
                0,
                1,
                TerminalReason: BuildTerminalReason.AgentException,
                TransactionRolledBack: true,
                FinalValidationStatus: BuildValidationStatus.Cancelled,
                ValidationFailureSummary: "provider failed"));

        failed.State.Should().Be(BuildRunState.Failed);
        failed.TerminalReason.Should().Be(BuildTerminalReason.AgentException);
        failed.FailureSummary.Should().Be("provider failed");
    }

    [Fact]
    public async Task CompleteAsync_SkippedValidation_ProducesFailedRun()
    {
        var store = new InMemoryBuildRunStore();
        var sut = CreateCoordinator(store, "fingerprint-a");
        var run = await sut.BeginOrResumeAsync(
            SessionId.NewId(),
            "Fix Foo.cs line 42 and run FooTests.",
            Path.GetTempPath());
        run = await ApprovePlanAsync(sut, run);

        var completed = await sut.CompleteAsync(
            run.Id,
            new MainAgentRunResult(
                null,
                0,
                0,
                1,
                TerminalReason: BuildTerminalReason.ValidationFailed,
                TransactionRolledBack: true,
                FinalValidationStatus: BuildValidationStatus.Skipped,
                ModifiedFiles: ["Foo.cs"],
                ValidationFailureSummary: "No verification profile."));

        completed.State.Should().Be(BuildRunState.Failed);
        completed.TransactionCommitted.Should().BeFalse();
        completed.TransactionRolledBack.Should().BeTrue();
    }

    [Fact]
    public void BuildPlanValidator_RejectsUnknownDependenciesAndCycles()
    {
        var unknown = new BuildPlan(
            "invalid",
            [new BuildPlanTask("a", "A", "A", ["missing"], [], ["done"])],
            [],
            [],
            []);
        var cyclic = new BuildPlan(
            "invalid",
            [
                new BuildPlanTask("a", "A", "A", ["b"], [], ["done"]),
                new BuildPlanTask("b", "B", "B", ["a"], [], ["done"]),
            ],
            [],
            [],
            []);

        var unknownAct = () => BuildPlanValidator.Validate(unknown);
        var cyclicAct = () => BuildPlanValidator.Validate(cyclic);

        unknownAct.Should().Throw<BuildPlanValidationException>()
            .WithMessage("*unknown task 'missing'*");
        cyclicAct.Should().Throw<BuildPlanValidationException>()
            .WithMessage("*contains a cycle*");
    }

    [Fact]
    public async Task BeginOrResumeAsync_PrescribedPlan_PersistsTaskDagAndMappings()
    {
        var store = new InMemoryBuildRunStore();
        var tasks = new TaskService();
        var sut = CreateCoordinator(store, "fingerprint-a", tasks);
        var conversationId = SessionId.NewId();

        var run = await sut.BeginOrResumeAsync(
            conversationId,
            "Execute approved Foo.cs and FooTests.cs plan.",
            Path.GetTempPath(),
            TestContext.Current.CancellationToken,
            prescribedPlan: CreatePrescribedPlan());
        var scoped = tasks.ListTasks(
            conversationId: conversationId.ToString(),
            buildRunId: run.Id.ToString(),
            exactScope: true);

        run.Plan!.RequireExplicitTaskCompletion.Should().BeTrue();
        run.Plan.Tasks.Should().HaveCount(2);
        scoped.Should().HaveCount(2);
        var implementation = run.Plan.Tasks.Single(task => task.Id == "implementation");
        var verification = run.Plan.Tasks.Single(task => task.Id == "verification");
        implementation.TaskItemId.Should().NotBeNullOrWhiteSpace();
        verification.TaskItemId.Should().NotBeNullOrWhiteSpace();
        tasks.GetTask(verification.TaskItemId!)!.BlockedBy.Should().ContainSingle()
            .Which.Should().Be(implementation.TaskItemId);
    }

    [Fact]
    public async Task CompleteAsync_PrescribedPlan_RejectsImplicitTaskCompletion()
    {
        var store = new InMemoryBuildRunStore();
        var tasks = new TaskService();
        var sut = CreateCoordinator(store, "fingerprint-a", tasks);
        var run = await sut.BeginOrResumeAsync(
            SessionId.NewId(),
            "Execute approved Foo.cs and FooTests.cs plan.",
            Path.GetTempPath(),
            TestContext.Current.CancellationToken,
            prescribedPlan: CreatePrescribedPlan());
        run = await ApprovePlanAsync(sut, run);

        var failed = await sut.CompleteAsync(
            run.Id,
            new MainAgentRunResult(
                "done",
                10,
                5,
                2,
                TerminalReason: BuildTerminalReason.Completed,
                FinalValidationStatus: BuildValidationStatus.Passed,
                ModifiedFiles: ["Foo.cs", "FooTests.cs"]),
            TestContext.Current.CancellationToken);

        failed.State.Should().Be(BuildRunState.Failed);
        failed.TerminalReason.Should().Be(BuildTerminalReason.ValidationFailed);
        failed.FailureSummary.Should().Contain("explicitly completed");
    }

    [Fact]
    public async Task CompleteAsync_PrescribedPlan_UsesTaskSpecificFileEvidence()
    {
        var store = new InMemoryBuildRunStore();
        var tasks = new TaskService();
        var sut = CreateCoordinator(store, "fingerprint-a", tasks);
        var run = await sut.BeginOrResumeAsync(
            SessionId.NewId(),
            "Execute approved Foo.cs and FooTests.cs plan.",
            Path.GetTempPath(),
            TestContext.Current.CancellationToken,
            prescribedPlan: CreatePrescribedPlan());
        run = await ApprovePlanAsync(sut, run);
        foreach (var task in run.Plan!.Tasks)
            tasks.UpdateTask(task.TaskItemId!, status: TaskStatus.Completed).Should().BeTrue();

        var accepting = await sut.CompleteAsync(
            run.Id,
            new MainAgentRunResult(
                "done",
                10,
                5,
                2,
                TerminalReason: BuildTerminalReason.Completed,
                FinalValidationStatus: BuildValidationStatus.Passed,
                ModifiedFiles: ["Foo.cs", "FooTests.cs"]),
            TestContext.Current.CancellationToken);

        accepting.State.Should().Be(BuildRunState.Accepting);
        accepting.Plan!.Tasks.Single(task => task.Id == "implementation").CompletionEvidence
            .Where(evidence => evidence.Kind == BuildEvidenceKind.FileChange)
            .Should().ContainSingle(evidence => evidence.Reference == "Foo.cs");
        accepting.Plan.Tasks.Single(task => task.Id == "verification").CompletionEvidence
            .Where(evidence => evidence.Kind == BuildEvidenceKind.FileChange)
            .Should().ContainSingle(evidence => evidence.Reference == "FooTests.cs");
    }

    [Fact]
    public async Task CompleteAsync_PrescribedPlan_RejectsUnattributedChangedFile()
    {
        var store = new InMemoryBuildRunStore();
        var tasks = new TaskService();
        var sut = CreateCoordinator(store, "fingerprint-a", tasks);
        var run = await sut.BeginOrResumeAsync(
            SessionId.NewId(),
            "Execute approved Foo.cs and FooTests.cs plan.",
            Path.GetTempPath(),
            TestContext.Current.CancellationToken,
            prescribedPlan: CreatePrescribedPlan());
        run = await ApprovePlanAsync(sut, run);
        foreach (var task in run.Plan!.Tasks)
        {
            tasks.ProjectTaskStatus(
                task.TaskItemId!,
                TaskStatus.Completed,
                $"{task.Id} acceptance evidence",
                $"plan:2:{task.Id}",
                requireCompletedDependencies: true).Succeeded.Should().BeTrue();
        }

        var failed = await sut.CompleteAsync(
            run.Id,
            new MainAgentRunResult(
                "done",
                10,
                5,
                2,
                TerminalReason: BuildTerminalReason.Completed,
                FinalValidationStatus: BuildValidationStatus.Passed,
                ModifiedFiles: ["Foo.cs", "FooTests.cs", "Unplanned.cs"]),
            TestContext.Current.CancellationToken);

        failed.State.Should().Be(BuildRunState.Failed);
        failed.FailureSummary.Should().Contain("Unplanned.cs").And.Contain("not attributed");
    }

    [Fact]
    public async Task CompleteAsync_PrescribedPlan_MapsProjectedStepEvidenceToTaskAcceptance()
    {
        var store = new InMemoryBuildRunStore();
        var tasks = new TaskService();
        var sut = CreateCoordinator(store, "fingerprint-a", tasks);
        var run = await sut.BeginOrResumeAsync(
            SessionId.NewId(),
            "Execute approved Foo.cs and FooTests.cs plan.",
            Path.GetTempPath(),
            TestContext.Current.CancellationToken,
            prescribedPlan: CreatePrescribedPlan());
        run = await ApprovePlanAsync(sut, run);
        foreach (var task in run.Plan!.Tasks)
        {
            var projected = tasks.ProjectTaskStatus(
                task.TaskItemId!,
                TaskStatus.Completed,
                $"{task.Id} acceptance evidence",
                $"plan:2:{task.Id}",
                requireCompletedDependencies: true);
            projected.Succeeded.Should().BeTrue();
        }

        var accepting = await sut.CompleteAsync(
            run.Id,
            new MainAgentRunResult(
                "done",
                10,
                5,
                2,
                TerminalReason: BuildTerminalReason.Completed,
                FinalValidationStatus: BuildValidationStatus.Passed,
                ModifiedFiles: ["Foo.cs", "FooTests.cs"]),
            TestContext.Current.CancellationToken);

        accepting.State.Should().Be(BuildRunState.Accepting);
        accepting.Plan!.Tasks.Should().OnlyContain(task =>
            task.CompletionEvidence.Any(evidence => evidence.Kind == BuildEvidenceKind.Acceptance));
        accepting.Scope!.AcceptanceCriteria
            .Where(item => item.Id.StartsWith("task:", StringComparison.Ordinal))
            .Should().OnlyContain(item =>
                item.Status == AcceptanceStatus.Passed
                && item.Evidence!.Contains("acceptance evidence", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ApprovePlanAsync_FromPlanned_PersistsPolicyAndEntersImplementing()
    {
        var store = new InMemoryBuildRunStore();
        var sut = CreateCoordinator(store, "fingerprint-a");
        var planned = await sut.BeginOrResumeAsync(
            SessionId.NewId(),
            "Fix Foo.cs line 42 and run FooTests.",
            Path.GetTempPath());

        var implementing = await sut.ApprovePlanAsync(
            planned.Id,
            new ApprovedToolPolicy(["ReadFile", "Write", "Edit"]),
            "runtime-approved",
            TestContext.Current.CancellationToken);

        implementing.State.Should().Be(BuildRunState.Implementing);
        implementing.ApprovedToolPolicy!.ToolNames.Should().Contain("Write");
        implementing.PlanApprovedAt.Should().NotBeNull();
        implementing.PlanApprovalSource.Should().Be("runtime-approved");
        implementing.PlanRejectionReason.Should().BeNull();
        (await store.LoadByIdAsync(planned.Id, TestContext.Current.CancellationToken))!
            .State.Should().Be(BuildRunState.Implementing);
    }

    [Fact]
    public async Task ApprovePlanAsync_RejectsNonPlannedState()
    {
        var store = new InMemoryBuildRunStore();
        var sut = CreateCoordinator(store, "fingerprint-a");
        var run = await sut.BeginOrResumeAsync(
            SessionId.NewId(),
            "Fix Foo.cs line 42 and run FooTests.",
            Path.GetTempPath());
        var implementing = await sut.ApprovePlanAsync(
            run.Id,
            new ApprovedToolPolicy(["ReadFile"]),
            "runtime-approved",
            TestContext.Current.CancellationToken);

        var act = () => sut.ApprovePlanAsync(
            implementing.Id,
            new ApprovedToolPolicy(["ReadFile"]),
            "runtime-approved",
            TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*from state 'Implementing'*");
    }

    [Fact]
    public async Task ApprovePlanAsync_RejectsEmptyPolicy()
    {
        var store = new InMemoryBuildRunStore();
        var sut = CreateCoordinator(store, "fingerprint-a");
        var planned = await sut.BeginOrResumeAsync(
            SessionId.NewId(),
            "Fix Foo.cs line 42 and run FooTests.",
            Path.GetTempPath());

        var act = () => sut.ApprovePlanAsync(
            planned.Id,
            new ApprovedToolPolicy([]),
            "runtime-approved",
            TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*at least one tool*");
    }

    [Fact]
    public async Task ApprovePlanAsync_WorkspaceDrift_BlocksInsteadOfExecuting()
    {
        var store = new InMemoryBuildRunStore();
        var provider = Substitute.For<IWorkspaceFingerprintProvider>();
        provider.ComputeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(
                Task.FromResult("fingerprint-a"),
                Task.FromResult("fingerprint-drifted"));
        var sut = CreateCoordinator(store, provider);
        var planned = await sut.BeginOrResumeAsync(
            SessionId.NewId(),
            "Fix Foo.cs line 42 and run FooTests.",
            Path.GetTempPath());
        planned.State.Should().Be(BuildRunState.Planned);

        var blocked = await sut.ApprovePlanAsync(
            planned.Id,
            new ApprovedToolPolicy(["ReadFile"]),
            "runtime-approved",
            TestContext.Current.CancellationToken);

        blocked.State.Should().Be(BuildRunState.Blocked);
        blocked.PlanRejectionReason.Should().Be("workspace-drift");
        blocked.FailureSummary.Should().Contain("Workspace changed");
    }

    [Fact]
    public async Task RejectPlanAsync_BlocksWithReason()
    {
        var store = new InMemoryBuildRunStore();
        var sut = CreateCoordinator(store, "fingerprint-a");
        var planned = await sut.BeginOrResumeAsync(
            SessionId.NewId(),
            "Fix Foo.cs line 42 and run FooTests.",
            Path.GetTempPath());

        var blocked = await sut.RejectPlanAsync(
            planned.Id,
            "用户拒绝计划",
            TestContext.Current.CancellationToken);

        blocked.State.Should().Be(BuildRunState.Blocked);
        blocked.PlanRejectionReason.Should().Be("用户拒绝计划");
        blocked.FailureSummary.Should().Contain("拒绝");
        blocked.TerminalReason.Should().Be(BuildTerminalReason.Blocked);
    }

    private static async Task<BuildRun> ApprovePlanAsync(
        BuildRunCoordinator sut,
        BuildRun planned,
        CancellationToken ct = default)
    {
        planned.State.Should().Be(BuildRunState.Planned);
        return await sut.ApprovePlanAsync(
            planned.Id,
            new ApprovedToolPolicy(["ReadFile", "Write", "Edit", "Bash", "PowerShell"]),
            "test",
            ct);
    }

    private static BuildPlan CreatePrescribedPlan()
        => new(
            "approved plan",
            [
                new BuildPlanTask(
                    "implementation",
                    "Implement fix",
                    "Fix Foo.cs",
                    [],
                    ["Foo.cs"],
                    ["Foo behavior is fixed"]),
                new BuildPlanTask(
                    "verification",
                    "Add tests",
                    "Add FooTests.cs",
                    ["implementation"],
                    ["FooTests.cs"],
                    ["Foo tests pass"]),
            ],
            ["dotnet test"],
            [],
            [],
            RequireExplicitTaskCompletion: true);

    private static BuildRunCoordinator CreateCoordinator(
        IBuildRunStore store,
        string fingerprint,
        TaskService? taskService = null)
    {
        var provider = Substitute.For<IWorkspaceFingerprintProvider>();
        provider.ComputeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(fingerprint));
        return CreateCoordinator(store, provider, taskService);
    }

    private static BuildRunCoordinator CreateCoordinator(
        IBuildRunStore store,
        IWorkspaceFingerprintProvider provider,
        TaskService? taskService = null) =>
        new(
            store,
            provider,
            new RequirementAssessmentService(),
            new BuildStateTransitionService(),
            taskService ?? new TaskService(),
            Substitute.For<ILogger<BuildRunCoordinator>>());

    private static BuildRun CreateAcceptingRun()
    {
        var now = DateTimeOffset.UtcNow;
        return new BuildRun
        {
            Id = BuildRunId.New(),
            ConversationId = SessionId.NewId(),
            State = BuildRunState.Accepting,
            IntakePrompt = "Fix Foo.cs and run tests.",
            Scope = new BuildScopeSnapshot(
                "Fix Foo.cs",
                ["Foo.cs"],
                [],
                [],
                [new AcceptanceCriterion("a1", "Tests pass", true, AcceptanceStatus.Passed, "1/1")],
                "user",
                now),
            Plan = new BuildPlan(
                "fix",
                [new BuildPlanTask(
                    "t1",
                    "Fix",
                    "Fix",
                    [],
                    ["Foo.cs"],
                    ["a1"],
                    BuildTaskStatus.Completed,
                    [
                        new BuildTaskEvidence(BuildEvidenceKind.TaskCompletion, "task-1", "Persistent task completed."),
                        new BuildTaskEvidence(BuildEvidenceKind.Validation, "v1", "Tests passed."),
                    ],
                    TaskItemId: "task-1")],
                ["dotnet test"],
                [],
                []),
            ChangedFiles = ["Foo.cs"],
            DeliveryManifest = new BuildDeliveryManifest(
                ["Foo.cs"],
                ["t1"],
                ["Tests passed."],
                ["1/1"],
                [],
                now),
            TransactionCommitted = true,
            TerminalReason = BuildTerminalReason.Completed,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    [Fact]
    public async Task JsonStore_ApprovedToolPolicy_RoundTrips()
    {
        var store = new JsonBuildRunStore(_root);
        var now = DateTimeOffset.UtcNow;
        var run = new BuildRun
        {
            Id = BuildRunId.New(),
            ConversationId = SessionId.NewId(),
            State = BuildRunState.Implementing,
            ApprovedToolPolicy = new ApprovedToolPolicy(["ReadFile", "Write", "Edit"]),
            PlanApprovedAt = now,
            PlanApprovalSource = "runtime-approved",
            CreatedAt = now,
            UpdatedAt = now,
        };

        await store.SaveAsync(run, 0, TestContext.Current.CancellationToken);

        var saved = await store.LoadByIdAsync(run.Id, TestContext.Current.CancellationToken);
        saved!.ApprovedToolPolicy!.ToolNames.Should().BeEquivalentTo(["ReadFile", "Write", "Edit"]);
        saved.PlanApprovalSource.Should().Be("runtime-approved");
        saved.PlanApprovedAt.Should().Be(now);
        saved.State.Should().Be(BuildRunState.Implementing);
    }

    [Fact]
    public async Task JsonStore_FirstSaveWithNonZeroSequence_UsesAggregateVersionForCas()
    {
        var store = new JsonBuildRunStore(_root);
        var now = DateTimeOffset.UtcNow;
        var run = new BuildRun
        {
            Id = BuildRunId.New(),
            ConversationId = SessionId.NewId(),
            State = BuildRunState.Implementing,
            SequenceNumber = 7,
            CreatedAt = now,
            UpdatedAt = now,
            Version = 0,
        };

        await store.SaveAsync(run, 0, TestContext.Current.CancellationToken);

        var saved = await store.LoadByIdAsync(run.Id, TestContext.Current.CancellationToken);
        saved!.Version.Should().Be(1);
        saved.SequenceNumber.Should().Be(7);
    }

    [Fact]
    public async Task JsonStore_CorruptPrimary_RecoversValidBackup()
    {
        var store = new JsonBuildRunStore(_root);
        var now = DateTimeOffset.UtcNow;
        var conversationId = SessionId.NewId();
        var run = new BuildRun
        {
            Id = BuildRunId.New(),
            ConversationId = conversationId,
            State = BuildRunState.Implementing,
            CreatedAt = now,
            UpdatedAt = now,
        };
        await store.SaveAsync(run, 0, TestContext.Current.CancellationToken);
        var first = (await store.LoadAsync(conversationId, TestContext.Current.CancellationToken))!;
        await store.SaveAsync(
            first with { SequenceNumber = 1 },
            first.Version,
            TestContext.Current.CancellationToken);
        var path = Path.Combine(_root, conversationId.Value, "run.json");
        File.WriteAllText(path, "{ corrupt");

        var recovered = await store.LoadAsync(conversationId, TestContext.Current.CancellationToken);

        recovered.Should().NotBeNull();
        recovered!.Id.Should().Be(run.Id);
        recovered.Version.Should().Be(2);
        recovered.SequenceNumber.Should().Be(1);
    }

    [Fact]
    public async Task JsonStore_Replay_ReturnsLatestPersistedAggregate()
    {
        var store = new JsonBuildRunStore(_root);
        var now = DateTimeOffset.UtcNow;
        var run = new BuildRun
        {
            Id = BuildRunId.New(),
            ConversationId = SessionId.NewId(),
            State = BuildRunState.Implementing,
            SequenceNumber = 7,
            CreatedAt = now,
            UpdatedAt = now,
        };
        await store.SaveAsync(run, 0, TestContext.Current.CancellationToken);
        var first = (await store.LoadByIdAsync(run.Id, TestContext.Current.CancellationToken))!;
        await store.SaveAsync(
            first with
            {
                State = BuildRunState.Recovering,
                SequenceNumber = 8,
            },
            first.Version,
            TestContext.Current.CancellationToken);

        var events = await store.LoadEventsAsync(run.Id, TestContext.Current.CancellationToken);
        var replayed = await store.ReplayAsync(run.Id, TestContext.Current.CancellationToken);

        events.Should().HaveCount(2);
        events.Select(item => item.EventId).Should().OnlyHaveUniqueItems();
        events.Select(item => item.Version).Should().ContainInOrder(1, 2);
        events[1].FromState.Should().Be(BuildRunState.Implementing);
        events[1].ToState.Should().Be(BuildRunState.Recovering);
        replayed.Should().NotBeNull();
        replayed!.Version.Should().Be(2);
        replayed.State.Should().Be(BuildRunState.Recovering);
        replayed.SequenceNumber.Should().Be(8);
    }

    [Fact]
    public async Task JsonStore_EventSequenceWithoutCheckpoint_RecoversLatestAggregate()
    {
        var store = new JsonBuildRunStore(_root);
        var now = DateTimeOffset.UtcNow;
        var conversationId = SessionId.NewId();
        var run = new BuildRun
        {
            Id = BuildRunId.New(),
            ConversationId = conversationId,
            State = BuildRunState.Implementing,
            CreatedAt = now,
            UpdatedAt = now,
        };
        await store.SaveAsync(run, 0, TestContext.Current.CancellationToken);
        var path = Path.Combine(_root, conversationId.Value, "run.json");
        File.Delete(path);

        var recovered = await store.LoadAsync(conversationId, TestContext.Current.CancellationToken);

        recovered.Should().NotBeNull();
        recovered!.Id.Should().Be(run.Id);
        recovered.Version.Should().Be(1);
        recovered.State.Should().Be(BuildRunState.Implementing);
    }

    [Fact]
    public async Task JsonStore_SeparateInstances_RejectConcurrentStaleVersion()
    {
        var firstStore = new JsonBuildRunStore(_root);
        var secondStore = new JsonBuildRunStore(_root);
        var now = DateTimeOffset.UtcNow;
        var run = new BuildRun
        {
            Id = BuildRunId.New(),
            ConversationId = SessionId.NewId(),
            State = BuildRunState.Implementing,
            CreatedAt = now,
            UpdatedAt = now,
        };
        await firstStore.SaveAsync(run, 0, TestContext.Current.CancellationToken);
        var current = (await firstStore.LoadByIdAsync(run.Id, TestContext.Current.CancellationToken))!;

        var saves = new[]
        {
            firstStore.SaveAsync(current with { SequenceNumber = 1 }, 1, TestContext.Current.CancellationToken),
            secondStore.SaveAsync(current with { SequenceNumber = 2 }, 1, TestContext.Current.CancellationToken),
        };
        var results = await Task.WhenAll(saves.Select(async save =>
        {
            try
            {
                await save;
                return true;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }));

        results.Count(succeeded => succeeded).Should().Be(1);
        results.Count(succeeded => !succeeded).Should().Be(1);
        var events = await firstStore.LoadEventsAsync(run.Id, TestContext.Current.CancellationToken);
        events.Select(item => item.Version).Should().ContainInOrder(1, 2);
    }

    [Fact]
    public async Task JsonStore_CorruptEventPrimaryAndBackup_FailsClosed()
    {
        var store = new JsonBuildRunStore(_root);
        var now = DateTimeOffset.UtcNow;
        var conversationId = SessionId.NewId();
        var run = new BuildRun
        {
            Id = BuildRunId.New(),
            ConversationId = conversationId,
            State = BuildRunState.Implementing,
            CreatedAt = now,
            UpdatedAt = now,
        };
        await store.SaveAsync(run, 0, TestContext.Current.CancellationToken);
        var first = (await store.LoadAsync(conversationId, TestContext.Current.CancellationToken))!;
        await store.SaveAsync(first, first.Version, TestContext.Current.CancellationToken);
        var path = Path.Combine(_root, conversationId.Value, "events.json");
        File.WriteAllText(path, "{ corrupt-primary");
        File.WriteAllText(path + ".bak", "{ corrupt-backup");

        var act = () => store.LoadAsync(conversationId, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidDataException>()
            .WithMessage("*event sequence*corrupt or incompatible*");
    }

    [Fact]
    public async Task JsonStore_CorruptPrimaryAndBackup_FailsClosed()
    {
        var store = new JsonBuildRunStore(_root);
        var now = DateTimeOffset.UtcNow;
        var conversationId = SessionId.NewId();
        var run = new BuildRun
        {
            Id = BuildRunId.New(),
            ConversationId = conversationId,
            State = BuildRunState.Implementing,
            CreatedAt = now,
            UpdatedAt = now,
        };
        await store.SaveAsync(run, 0, TestContext.Current.CancellationToken);
        var first = (await store.LoadAsync(conversationId, TestContext.Current.CancellationToken))!;
        await store.SaveAsync(first, first.Version, TestContext.Current.CancellationToken);
        var path = Path.Combine(_root, conversationId.Value, "run.json");
        File.WriteAllText(path, "{ corrupt-primary");
        File.WriteAllText(path + ".bak", "{ corrupt-backup");

        var act = () => store.LoadAsync(conversationId, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidDataException>()
            .WithMessage("*corrupt or incompatible*");
    }

    [Fact]
    public async Task JsonStore_ConcurrentSave_RejectsStaleVersion()
    {
        var store = new JsonBuildRunStore(_root);
        var now = DateTimeOffset.UtcNow;
        var run = new BuildRun
        {
            Id = BuildRunId.New(),
            ConversationId = SessionId.NewId(),
            State = BuildRunState.Implementing,
            CreatedAt = now,
            UpdatedAt = now,
            Version = 0,
        };
        await store.SaveAsync(run, 0, TestContext.Current.CancellationToken);
        var current = (await store.LoadByIdAsync(run.Id, TestContext.Current.CancellationToken))!;
        await store.SaveAsync(current with { SequenceNumber = 1 }, 1, TestContext.Current.CancellationToken);

        var act = () => store.SaveAsync(
            current with { SequenceNumber = 2 },
            1,
            TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Concurrency conflict*");
    }

    [Fact]
    public async Task Coordinator_ClaimedRun_RejectsMissingAndStaleAttemptTokens()
    {
        var store = new JsonBuildRunStore(_root);
        var initial = CreatePersistableRun();
        await store.SaveAsync(initial, 0, TestContext.Current.CancellationToken);
        var persisted = (await store.LoadByIdAsync(initial.Id, TestContext.Current.CancellationToken))!;
        var claimed = await store.ClaimWorkflowAsync(
            initial.Id,
            fencingToken: 8,
            persisted.Version,
            TestContext.Current.CancellationToken);
        var coordinator = CreateCoordinator(store, claimed.WorkspaceFingerprint!);

        var missing = () => coordinator.BeginVerificationAsync(
            claimed.Id,
            TestContext.Current.CancellationToken);
        await missing.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Stale*token*");

        var stale = () => coordinator.BeginVerificationAsync(
            claimed.Id,
            TestContext.Current.CancellationToken,
            expectedWorkflowFencingToken: 7);
        await stale.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Stale*token*");

        var verifying = await coordinator.BeginVerificationAsync(
            claimed.Id,
            TestContext.Current.CancellationToken,
            expectedWorkflowFencingToken: 8);
        verifying.State.Should().Be(BuildRunState.Verifying);
        verifying.WorkflowFencingToken.Should().Be(8);
    }

    [Fact]
    public async Task JsonStore_WorkflowClaim_RequiresMonotonicFencingToken()
    {
        var store = new JsonBuildRunStore(_root);
        var run = CreatePersistableRun();
        await store.SaveAsync(run, 0, TestContext.Current.CancellationToken);
        var persisted = (await store.LoadByIdAsync(run.Id, TestContext.Current.CancellationToken))!;

        var firstClaim = await store.ClaimWorkflowAsync(
            run.Id,
            fencingToken: 10,
            persisted.Version,
            TestContext.Current.CancellationToken);
        firstClaim.WorkflowFencingToken.Should().Be(10);

        var staleClaim = () => store.ClaimWorkflowAsync(
            run.Id,
            fencingToken: 9,
            firstClaim.Version,
            TestContext.Current.CancellationToken);
        await staleClaim.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Stale*token*");

        var takeover = await store.ClaimWorkflowAsync(
            run.Id,
            fencingToken: 11,
            firstClaim.Version,
            TestContext.Current.CancellationToken);
        takeover.WorkflowFencingToken.Should().Be(11);
    }

    [Fact]
    public async Task JsonStore_ClaimedRun_RejectsUnfencedAndStaleWrites()
    {
        var store = new JsonBuildRunStore(_root);
        var run = CreatePersistableRun();
        await store.SaveAsync(run, 0, TestContext.Current.CancellationToken);
        var persisted = (await store.LoadByIdAsync(run.Id, TestContext.Current.CancellationToken))!;
        var claimed = await store.ClaimWorkflowAsync(
            run.Id,
            fencingToken: 20,
            persisted.Version,
            TestContext.Current.CancellationToken);

        var unfenced = () => store.SaveAsync(
            claimed with { SequenceNumber = 1 },
            claimed.Version,
            TestContext.Current.CancellationToken);
        await unfenced.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Stale*token*");

        var stale = () => store.SaveFencedAsync(
            claimed with { SequenceNumber = 1, WorkflowFencingToken = 19 },
            claimed.Version,
            fencingToken: 19,
            TestContext.Current.CancellationToken);
        await stale.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Stale*token*");

        await store.SaveFencedAsync(
            claimed with { SequenceNumber = 1 },
            claimed.Version,
            fencingToken: 20,
            TestContext.Current.CancellationToken);
        var saved = await store.LoadByIdAsync(run.Id, TestContext.Current.CancellationToken);
        saved!.SequenceNumber.Should().Be(1);
        saved.WorkflowFencingToken.Should().Be(20);
    }

    private BuildRun CreatePersistableRun()
    {
        var now = DateTimeOffset.UtcNow;
        return new BuildRun
        {
            Id = BuildRunId.New(),
            ConversationId = SessionId.NewId(),
            State = BuildRunState.Implementing,
            IntakePrompt = "implement",
            WorkingDirectory = _root,
            WorkspaceFingerprint = "fingerprint",
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    private sealed class InMemoryBuildRunStore : IBuildRunStore
    {
        private readonly Dictionary<SessionId, BuildRun> _runs = [];

        public Task<BuildRun?> LoadAsync(SessionId? conversationId, CancellationToken ct = default) =>
            Task.FromResult(conversationId is { } id && _runs.TryGetValue(id, out var run) ? run : null);

        public Task SaveAsync(BuildRun run, long expectedVersion, CancellationToken ct = default)
        {
            if (run.ConversationId is not { } id)
                throw new InvalidOperationException();
            if (_runs.TryGetValue(id, out var existing) && existing.Version != expectedVersion)
                throw new InvalidOperationException("Concurrency conflict.");
            _runs[id] = run with { Version = expectedVersion + 1 };
            return Task.CompletedTask;
        }

        public async Task<BuildRun> ClaimWorkflowAsync(
            BuildRunId runId,
            long fencingToken,
            long expectedVersion,
            CancellationToken ct = default)
        {
            var current = await LoadByIdAsync(runId, ct)
                ?? throw new InvalidOperationException();
            if (current.Version != expectedVersion
                || current.WorkflowFencingToken is { } existingToken && fencingToken <= existingToken)
            {
                throw new InvalidOperationException("Stale BuildRun workflow fencing token.");
            }
            var claimed = current with { WorkflowFencingToken = fencingToken };
            _runs[current.ConversationId!.Value] = claimed with { Version = expectedVersion + 1 };
            return _runs[current.ConversationId.Value];
        }

        public Task SaveFencedAsync(
            BuildRun run,
            long expectedVersion,
            long fencingToken,
            CancellationToken ct = default)
        {
            if (run.ConversationId is not { } id
                || !_runs.TryGetValue(id, out var existing)
                || existing.Version != expectedVersion
                || existing.WorkflowFencingToken != fencingToken
                || run.WorkflowFencingToken != fencingToken)
            {
                throw new InvalidOperationException("Stale BuildRun workflow fencing token.");
            }
            _runs[id] = run with { Version = expectedVersion + 1 };
            return Task.CompletedTask;
        }

        public Task<BuildRun?> LoadByIdAsync(BuildRunId id, CancellationToken ct = default) =>
            Task.FromResult(_runs.Values.SingleOrDefault(run => run.Id == id));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }
}
