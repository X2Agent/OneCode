using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using OneCode.App.Services.Agent;
using OneCode.App.Services.Coordinator;
using OneCode.Core.Coordinator;
using OneCode.Core.Errors;
using OneCode.Infrastructure.Teams;
using OneCode.Infrastructure.Workflows;

namespace OneCode.Tests;

public sealed class TeamTaskWorkflowTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "onecode-tests", "team-task-workflow", Guid.NewGuid().ToString("N"));

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
    public void Compile_TaskInputOrderDoesNotChangeDefinitionHash()
    {
        var compiler = new TeamTaskWorkflowCompiler();
        var first = CreateRun([
            TaskDef("design", [], TeamToolPolicy.ReadOnly),
            TaskDef("implement", ["design"], TeamToolPolicy.WriteAllowed),
            TaskDef("review", ["implement"], TeamToolPolicy.ReadOnly),
        ]);
        var second = CreateRun([
            TaskDef("review", ["implement"], TeamToolPolicy.ReadOnly),
            TaskDef("implement", ["design"], TeamToolPolicy.WriteAllowed),
            TaskDef("design", [], TeamToolPolicy.ReadOnly),
        ]);

        var firstDefinition = compiler.Compile(first, CreateConfig(), "model", new FakeRuntime());
        var secondDefinition = compiler.Compile(second, CreateConfig(), "model", new FakeRuntime());

        firstDefinition.Registration.DefinitionHash.Should().Be(secondDefinition.Registration.DefinitionHash);
        // 两个输入顺序不同的定义等同，但它们是不同的 TeamRun 实例（RunId 内嵌 run.Id），故 RunId 不同。
        firstDefinition.Registration.RunId.Should().StartWith("team/");
        secondDefinition.Registration.RunId.Should().StartWith("team/");
        firstDefinition.EffectiveDependencies.Should().BeEquivalentTo(secondDefinition.EffectiveDependencies);
    }

    [Fact]
    public void Compile_DefinitionInputsChangeHash()
    {
        var compiler = new TeamTaskWorkflowCompiler();
        var run = CreateRun([TaskDef("implementation", [], TeamToolPolicy.WriteAllowed)]);
        var baseline = compiler.Compile(run, CreateConfig(), "model-a", new FakeRuntime());

        compiler.Compile(run, CreateConfig(), "model-b", new FakeRuntime())
            .Registration.DefinitionHash.Should().NotBe(baseline.Registration.DefinitionHash);
        compiler.Compile(run, CreateConfig(TeamOrchestrationMode.Magentic), "model-a", new FakeRuntime())
            .Registration.DefinitionHash.Should().NotBe(baseline.Registration.DefinitionHash);
        compiler.Compile(
                CreateRun([TaskDef("implementation", [], TeamToolPolicy.ReadOnly)]),
                CreateConfig(),
                "model-a",
                new FakeRuntime())
            .Registration.DefinitionHash.Should().NotBe(baseline.Registration.DefinitionHash);
        compiler.Compile(run, CreateConfig(maxTurns: 99), "model-a", new FakeRuntime())
            .Registration.DefinitionHash.Should().NotBe(baseline.Registration.DefinitionHash);
    }

    [Fact]
    public void Validate_RejectsDuplicateUnknownCyclicAndCollidingGraphs()
    {
        var duplicate = () => TeamTaskWorkflowCompiler.Validate(
            [TaskDef("a", [], TeamToolPolicy.ReadOnly), TaskDef("a", [], TeamToolPolicy.ReadOnly)]);
        var unknown = () => TeamTaskWorkflowCompiler.Validate(
            [TaskDef("a", ["missing"], TeamToolPolicy.ReadOnly)]);
        var cycle = () => TeamTaskWorkflowCompiler.Validate(
            [TaskDef("a", ["b"], TeamToolPolicy.ReadOnly), TaskDef("b", ["a"], TeamToolPolicy.ReadOnly)]);
        var collision = () => TeamTaskWorkflowCompiler.Validate(
            [TaskDef("a b", [], TeamToolPolicy.ReadOnly), TaskDef("a-b", [], TeamToolPolicy.ReadOnly)]);

        duplicate.Should().Throw<InvalidOperationException>().WithMessage("*Duplicate*");
        unknown.Should().Throw<InvalidOperationException>().WithMessage("*unknown*");
        cycle.Should().Throw<InvalidOperationException>().WithMessage("*cycle*");
        collision.Should().Throw<InvalidOperationException>().WithMessage("*normalized*");
    }

    [Fact]
    public async Task Execute_DiamondDag_ParallelizesReadsAndSerializesWrites()
    {
        var runtime = new FakeRuntime();
        var host = CreateHost(out var teamStore, out _);
        var run = CreateRun([
            TaskDef("root", [], TeamToolPolicy.ReadOnly),
            TaskDef("read-a", ["root"], TeamToolPolicy.ReadOnly),
            TaskDef("read-b", ["root"], TeamToolPolicy.ReadOnly),
            TaskDef("write-a", ["read-a", "read-b"], TeamToolPolicy.WriteAllowed),
            TaskDef("write-b", ["write-a"], TeamToolPolicy.WriteAllowed),
        ]);
        await PersistAsync(teamStore, run);

        var result = await host.RunAsync(
            run, CreateConfig(), "model", runtime, new JsonSerializerOptions(),
            ct: TestContext.Current.CancellationToken);

        result.AllSucceeded.Should().BeTrue();
        result.Outcomes.Select(outcome => outcome.TaskId).Should().BeEquivalentTo(
            ["root", "read-a", "read-b", "write-a", "write-b"]);
        runtime.MaxReadOverlap.Should().BeGreaterThan(1, "read-only diamond branches must overlap");
        runtime.WriteOrder.Should().Equal("write-a", "write-b");
        runtime.BoundToken.Should().Be(result.Durable.Run.FencingToken);
    }

    [Fact]
    public async Task Execute_UpstreamFailureBlocksDownstreamAndClaimsBusinessRun()
    {
        var runtime = new FakeRuntime { FailingTaskIds = { "write-a" } };
        var host = CreateHost(out var teamStore, out _);
        var run = CreateRun([
            TaskDef("root", [], TeamToolPolicy.ReadOnly),
            TaskDef("write-a", ["root"], TeamToolPolicy.WriteAllowed),
            TaskDef("write-b", ["write-a"], TeamToolPolicy.WriteAllowed),
        ]);
        await PersistAsync(teamStore, run);

        var result = await host.RunAsync(
            run, CreateConfig(), "model", runtime, new JsonSerializerOptions(),
            ct: TestContext.Current.CancellationToken);

        result.AllSucceeded.Should().BeFalse();
        result.Outcomes.Single(outcome => outcome.TaskId == "write-a").Status
            .Should().Be(TeamTaskOutcomeStatus.Failed);
        result.Outcomes.Single(outcome => outcome.TaskId == "write-b").Status
            .Should().Be(TeamTaskOutcomeStatus.Blocked);
        runtime.Executed.Should().NotContain("write-b");

        var claimed = await teamStore.LoadAsync(run.Id, TestContext.Current.CancellationToken);
        claimed!.WorkflowFencingToken.Should().Be(result.Durable.Run.FencingToken);
        var staleSave = () => teamStore.SaveFencedAsync(
            claimed, claimed.Version, claimed.WorkflowFencingToken!.Value + 1,
            TestContext.Current.CancellationToken);
        await staleSave.Should().ThrowAsync<InvalidOperationException>().WithMessage("*fencing*");
    }

    [Fact]
    public async Task Resume_PreservesPersistedSuccessfulTaskWithoutReexecutingIt()
    {
        var runtime = new FakeRuntime();
        var host = CreateHost(out var teamStore, out _);
        var tasks = new[]
        {
            TaskDef("already-done", [], TeamToolPolicy.WriteAllowed),
            TaskDef("remaining", ["already-done"], TeamToolPolicy.ReadOnly),
        };
        var run = CreateRun(tasks) with
        {
            TaskGraph = new TeamTaskGraph(
                [
                    new TeamTaskState(tasks[0], TeamTaskStatus.Succeeded, Summary: "persisted evidence"),
                    new TeamTaskState(tasks[1], Status: null),
                ]),
        };
        await PersistAsync(teamStore, run);

        var result = await host.RunAsync(
            run,
            CreateConfig(),
            "model",
            runtime,
            new JsonSerializerOptions(),
            ct: TestContext.Current.CancellationToken);

        result.Outcomes.Single(outcome => outcome.TaskId == "already-done").Status
            .Should().Be(TeamTaskOutcomeStatus.Succeeded);
        runtime.Executed.Should().Equal("remaining");
    }

    private TeamTaskWorkflowHost CreateHost(out JsonTeamRunStore teamStore, out JsonWorkflowRunRegistry registry)
    {
        teamStore = new JsonTeamRunStore(Path.Combine(_root, "team-runs", Guid.NewGuid().ToString("N")));
        registry = new JsonWorkflowRunRegistry(Path.Combine(_root, "workflow-runs", Guid.NewGuid().ToString("N")));
        var checkpointFactory = new WorkflowCheckpointStoreFactory(
            Path.Combine(_root, "checkpoints", Guid.NewGuid().ToString("N")));
        var durableHost = new DurableWorkflowHost(
            registry,
            checkpointFactory,
            new WorkflowEventAdapter(),
            NullLogger<DurableWorkflowHost>.Instance);

        return new TeamTaskWorkflowHost(durableHost, new TeamTaskWorkflowCompiler(), teamStore, registry);
    }

    private static async Task PersistAsync(JsonTeamRunStore store, TeamRun run)
        => (await store.TrySaveAsync(run, expectedVersion: 0, TestContext.Current.CancellationToken))
            .Should().BeTrue();

    private static TeamConfig CreateConfig(
        TeamOrchestrationMode mode = TeamOrchestrationMode.GroupChat,
        int maxTurns = 10)
        => new(
            "team-a",
            "C:/teams/team-a.yaml",
            [new TeamMember("lead-1", "lead", null), new TeamMember("executor-1", "executor", null)],
            maxTurns,
            mode);

    private static TeamTaskDefinition TaskDef(string id, IReadOnlyList<string> dependsOn, TeamToolPolicy policy)
        => new(
            id,
            $"title-{id}",
            TeamTaskKind.Implementation,
            "executor",
            dependsOn,
            ["criterion"],
            policy);

    private static TeamRun CreateRun(IReadOnlyList<TeamTaskDefinition> tasks)
    {
        var now = DateTimeOffset.UtcNow;
        return new TeamRun
        {
            Id = TeamRunId.NewId(),
            TeamName = "team-a",
            OriginalRequest = "implement feature",
            WorkingDirectory = "C:/repo",
            Phase = TeamRunPhase.Execution,
            Status = TeamRunStatus.Running,
            Plan = new ImplementationPlan("plan", tasks, [], [], []),
            TaskGraph = new TeamTaskGraph(tasks
                .Select(task => new TeamTaskState(task, Status: null))
                .ToList()),
            PlanApproved = true,
            Version = 1,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    private sealed class FakeRuntime : ITeamTaskWorkflowRuntime
    {
        private int _activeReads;
        private readonly object _gate = new();

        public HashSet<string> FailingTaskIds { get; } = new(StringComparer.Ordinal);
        public List<string> Executed { get; } = [];
        public List<string> WriteOrder { get; } = [];
        public int MaxReadOverlap { get; private set; }
        public long? BoundToken { get; private set; }

        public Task BindAsync(TeamRun run, long fencingToken, CancellationToken ct)
        {
            BoundToken = fencingToken;
            return Task.CompletedTask;
        }

        public async Task<TeamRunResult> ExecuteTaskAsync(TeamTaskDefinition task, CancellationToken ct)
        {
            if (task.ToolPolicy == TeamToolPolicy.ReadOnly)
            {
                var active = Interlocked.Increment(ref _activeReads);
                lock (_gate)
                    MaxReadOverlap = Math.Max(MaxReadOverlap, active);
                await Task.Delay(50, ct);
                Interlocked.Decrement(ref _activeReads);
            }
            else
            {
                lock (_gate)
                    WriteOrder.Add(task.Id);
            }

            lock (_gate)
                Executed.Add(task.Id);

            if (FailingTaskIds.Contains(task.Id))
            {
                return new TeamRunResult(
                    "team-a",
                    "failed",
                    1,
                    false,
                    Error: AgentProblemDetails.ToolExecutionFailed("boom", toolName: "test"));
            }
            return new TeamRunResult("team-a", $"done-{task.Id}", 1, false);
        }
    }
}
