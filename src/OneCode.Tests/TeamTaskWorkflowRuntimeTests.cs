using Microsoft.Extensions.Logging.Abstractions;
using OneCode.App.Services.Coordinator;
using OneCode.Core.Coordinator;
using OneCode.Core.Errors;
using OneCode.Infrastructure.Agent;
using OneCode.Infrastructure.Teams;

namespace OneCode.Tests;

/// <summary>
/// C1: 越界检查必须只归属当前任务开始后的新增改动（CaptureChangeVersion /
/// GetModifiedFilesSince），前序任务的合法写入与后续只读任务不得被累积快照误判。
/// </summary>
public sealed class TeamTaskWorkflowRuntimeTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "onecode-tests", "team-runtime", Guid.NewGuid().ToString("N"));

    public ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        foreach (var dir in new[] { "dirA", "dirB", "dirC" })
            Directory.CreateDirectory(Path.Combine(_root, dir));
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task ExecuteTask_OutOfScopeCheck_AttributesOnlyCurrentTaskChanges()
    {
        var store = new JsonTeamRunStore(Path.Combine(_root, "team-runs"));
        var writeA = TaskDef("write-a", TeamToolPolicy.WriteAllowed, ["dirA"]);
        var writeB = TaskDef("write-b", TeamToolPolicy.WriteAllowed, ["dirB"]);
        var readC = TaskDef("read-c", TeamToolPolicy.ReadOnly, ["dirC"]);
        var run = await CreateClaimedRunAsync(store, [writeA, writeB, readC]);
        var service = CreateRunService(store, fingerprintProvider: null);
        var runner = new FakeTaskRunner
        {
            OnRun = (task, transaction) =>
            {
                // write-a: 合法写 dirA；write-b: 合法写 dirB 之外越界写 dirA/rogue.cs；read-c 不写。
                if (task.Id == "write-a") Write(transaction, Path.Combine(_root, "dirA", "file-a.cs"));
                if (task.Id == "write-b")
                {
                    Write(transaction, Path.Combine(_root, "dirB", "file-b.cs"));
                    Write(transaction, Path.Combine(_root, "dirA", "rogue.cs"));
                }
            },
        };
        using var runtime = new TeamTaskWorkflowRuntime(
            service, runner, CreateConfig(), _root, eventSink: null, imagePaths: null,
            static () => new EditTransaction(), ledger: null);
        await runtime.BindAsync(run, 1, TestContext.Current.CancellationToken);

        var resultA = await runtime.ExecuteTaskAsync(writeA, TestContext.Current.CancellationToken);
        resultA.HadFailures.Should().BeFalse("task A only writes inside its approved dirA scope");

        var resultC = await runtime.ExecuteTaskAsync(readC, TestContext.Current.CancellationToken);
        resultC.HadFailures.Should().BeFalse(
            "a read-only task following a write task must not inherit the write task's in-scope changes as its own");

        var resultB = await runtime.ExecuteTaskAsync(writeB, TestContext.Current.CancellationToken);
        resultB.HadFailures.Should().BeTrue("task B writes outside dirB");
        resultB.Error!.Detail.Should().Contain("rogue.cs");
        resultB.Error.Detail.Should().NotContain("file-a.cs",
            "the out-of-scope report must attribute only task B's changes, not task A's earlier in-scope writes");

        var persisted = await store.LoadAsync(run.Id, TestContext.Current.CancellationToken);
        persisted!.TaskGraph!.Tasks.Single(task => task.Definition.Id == "write-a").Status
            .Should().Be(TeamTaskStatus.Succeeded);
        persisted.TaskGraph.Tasks.Single(task => task.Definition.Id == "read-c").Status
            .Should().Be(TeamTaskStatus.Succeeded);
        persisted.TaskGraph.Tasks.Single(task => task.Definition.Id == "write-b").Status
            .Should().Be(TeamTaskStatus.Failed);
    }

    [Fact]
    public async Task ExecuteTask_AllChangesInScope_SucceedsAcrossWriteTasks()
    {
        var store = new JsonTeamRunStore(Path.Combine(_root, "team-runs-ok"));
        var writeA = TaskDef("write-a", TeamToolPolicy.WriteAllowed, ["dirA"]);
        var writeB = TaskDef("write-b", TeamToolPolicy.WriteAllowed, ["dirB"]);
        var run = await CreateClaimedRunAsync(store, [writeA, writeB]);
        var service = CreateRunService(store, fingerprintProvider: null);
        var runner = new FakeTaskRunner
        {
            OnRun = (task, transaction) =>
            {
                if (task.Id == "write-a") Write(transaction, Path.Combine(_root, "dirA", "file-a.cs"));
                if (task.Id == "write-b") Write(transaction, Path.Combine(_root, "dirB", "file-b.cs"));
            },
        };
        using var runtime = new TeamTaskWorkflowRuntime(
            service, runner, CreateConfig(), _root, eventSink: null, imagePaths: null,
            static () => new EditTransaction(), ledger: null);
        await runtime.BindAsync(run, 1, TestContext.Current.CancellationToken);

        (await runtime.ExecuteTaskAsync(writeA, TestContext.Current.CancellationToken)).HadFailures.Should().BeFalse();
        (await runtime.ExecuteTaskAsync(writeB, TestContext.Current.CancellationToken)).HadFailures
            .Should().BeFalse("disjoint AllowedPaths must both pass once attribution is per-task");
    }

    private static void Write(EditTransaction transaction, string path)
    {
        transaction.Snapshot(path);
        File.WriteAllText(path, "content");
    }

    private static TeamRunApplicationService CreateRunService(
        OneCode.Core.Coordinator.ITeamRunStore store,
        OneCode.Core.Build.IWorkspaceFingerprintProvider? fingerprintProvider)
        => new(
            store,
            new TeamRunStateMachine(),
            new TeamQualityGateRunner([]),
            new DeliveryReportBuilder(),
            fingerprintProvider);

    private async Task<TeamRun> CreateClaimedRunAsync(
        JsonTeamRunStore store,
        IReadOnlyList<TeamTaskDefinition> tasks)
    {
        var now = DateTimeOffset.UtcNow;
        var run = new TeamRun
        {
            Id = TeamRunId.NewId(),
            TeamName = "team-a",
            OriginalRequest = "implement feature",
            WorkingDirectory = _root,
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
        (await store.TrySaveAsync(run, expectedVersion: 0, TestContext.Current.CancellationToken))
            .Should().BeTrue();
        var saved = await store.LoadAsync(run.Id, TestContext.Current.CancellationToken);
        return await store.ClaimWorkflowAsync(run.Id, 1, saved!.Version, TestContext.Current.CancellationToken);
    }

    private static TeamConfig CreateConfig()
        => new(
            "team-a",
            "C:/teams/team-a.yaml",
            [new TeamMember("lead-1", "lead", null), new TeamMember("executor-1", "executor", null)],
            10,
            TeamOrchestrationMode.GroupChat);

    private static TeamTaskDefinition TaskDef(
        string id,
        TeamToolPolicy policy,
        IReadOnlyList<string> allowedPaths)
        => new(
            id,
            $"title-{id}",
            TeamTaskKind.Implementation,
            "executor",
            [],
            ["criterion"],
            policy,
            AllowedPaths: allowedPaths);

    private sealed class FakeTaskRunner : ITeamTaskWorkflowRunner
    {
        public Action<TeamTaskDefinition, EditTransaction>? OnRun { get; set; }

        public Task<TeamRunResult> RunTaskAsync(
            TeamConfig config,
            TeamTaskDefinition task,
            EditTransaction transaction,
            string cwd,
            Action<OrchestrationEvent>? eventSink,
            CancellationToken ct,
            IReadOnlyList<string>? imagePaths = null)
        {
            OnRun?.Invoke(task, transaction);
            return Task.FromResult(new TeamRunResult(config.TeamName, $"done-{task.Id}", 1, false));
        }
    }
}
