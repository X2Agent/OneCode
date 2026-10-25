
using Microsoft.Extensions.Logging;
using NSubstitute;
using OneCode.App.Services.BuildMode;
using OneCode.Core.Build;
using OneCode.Core.Tasks;
using OneCode.Infrastructure.Build;
using TaskStatus = OneCode.Core.Tasks.TaskStatus;
namespace OneCode.Tests;

public sealed partial class BuildRunCoordinatorTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "OneCodeBuildRunTests-" + Guid.NewGuid().ToString("N"));
    [Fact]
    public async Task BeginOrResumeAsync_PrescribedPlan_ParksAtPlannedForPreambleApproval()
    {
        // 预置计划（上层 PlanWorkflow 已批）走完评估 → 计划 → Planned 停靠；
        // BuildPreambleRunner 随后以 plan-mode-approval 源自动放行。
        var store = new InMemoryBuildRunStore();
        var sut = CreateCoordinator(store, "fingerprint-a");
        var prescribed = new BuildPlan(
            "Fix Foo.cs and run tests.",
            [new BuildPlanTask("t1", "Fix Foo.cs line 42", "Fix and verify", [], [], ["FooTests passes"])],
            ["dotnet test"], [], []);

        var run = await sut.BeginOrResumeAsync(
            SessionId.NewId(),
            "Fix Foo.cs line 42 and run FooTests.",
            Path.GetTempPath(),
            prescribedPlan: prescribed);

        run.State.Should().Be(BuildRunState.Planned);
        run.Plan.Should().NotBeNull();
        run.ApprovedToolPolicy.Should().BeNull();
        run = await ApprovePlanAsync(sut, run);
        run.State.Should().Be(BuildRunState.Implementing);
        run.Scope.Should().NotBeNull();
        run.TerminalReason.Should().BeNull();
        run.ApprovedToolPolicy.Should().NotBeNull();
        run.PlanApprovedAt.Should().NotBeNull();
        run.PlanApprovalSource.Should().Be("test");
    }

    [Fact]
    public async Task BeginOrResumeAsync_PersistsPlannedStateDirectly()
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
        observed.Should().Equal(BuildRunState.Planned);
        events.Select(item => item.ToState).Should().Equal(BuildRunState.Planned);
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

    [Fact]
    public async Task BeginOrResumeAsync_RepeatedCallOnPlannedRun_DoesNotDuplicatePersistentTask()
    {
        var store = new InMemoryBuildRunStore();
        var tasks = new TaskService();
        var sut = CreateCoordinator(store, "fingerprint-a", tasks);
        var conversationId = SessionId.NewId();
        var run = await sut.BeginOrResumeAsync(
            conversationId,
            "Fix Foo.cs line 42 and run FooTests.",
            Path.GetTempPath(),
            TestContext.Current.CancellationToken);

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

    private static async Task<BuildRun> ApprovePlanAsync(
        BuildRunCoordinator sut,
        BuildRun planned,
        CancellationToken ct = default)
    {
        planned.State.Should().Be(BuildRunState.Planned);
        return await sut.ApprovePlanAsync(
            planned.Id,
            new ApprovedToolPolicy(["ReadFile", "Write", "Edit", "Bash"]),
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
            []);

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
            new BuildStateTransitionService(),
            new BuildTaskLinker(taskService ?? new TaskService()),
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
            TerminalReason = RunTerminalReason.Completed,
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



