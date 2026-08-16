using System.Threading.Channels;
using Microsoft.Extensions.AI;
using NSubstitute;
using OneCode.App.Services.Agent;
using OneCode.App.Services.Coordinator;
using OneCode.App.Services.GoalMode;
using OneCode.App.Services.Streaming;
using OneCode.App.Tui;
using OneCode.Core.Build;
using OneCode.Core.Coordinator;
using OneCode.Core.Domain;
using OneCode.Core.Goals;
using OneCode.Core.Tools;
using OneCode.Core.Workflows;
using OneCode.Infrastructure.Build;
using OneCode.Infrastructure.Config;
using OneCode.Infrastructure.Teams;

namespace OneCode.Tests;

/// <summary>
/// C2 + H1: Succeeded 任务与 run 级文件事务的对账（resume 指纹校验）
/// 与用户取消的 Cancelled 终态落库。
/// </summary>
public sealed class TeamRunRecoveryTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "onecode-tests", "team-recovery", Guid.NewGuid().ToString("N"));

    public ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(Path.Combine(_root, "workspace"));
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
        return ValueTask.CompletedTask;
    }

    // --- C2: ReconcileSucceededTasksAsync ---

    [Fact]
    public async Task Reconcile_FingerprintMismatch_DemotesSucceededTasksForRerun()
    {
        var workspace = Path.Combine(_root, "workspace");
        await File.WriteAllTextAsync(
            Path.Combine(workspace, "code.cs"), "v1", TestContext.Current.CancellationToken);
        var provider = new WorkspaceFingerprintProvider();
        var expected = await provider.ComputeAsync(workspace, TestContext.Current.CancellationToken);

        var store = new JsonTeamRunStore(Path.Combine(_root, "runs-mismatch"));
        var run = await CreateClaimedRunAsync(store, "succeeded-task", expected);
        // 崩溃后 ledger reconcile 已回滚该任务的文件改动（此处以外部篡改模拟指纹漂移）。
        await File.WriteAllTextAsync(
            Path.Combine(workspace, "code.cs"), "rolled-back", TestContext.Current.CancellationToken);

        var service = CreateRunService(store, provider);
        var reconciled = await service.ReconcileSucceededTasksAsync(run, TestContext.Current.CancellationToken);

        reconciled.TaskGraph!.Tasks.Single(task => task.Definition.Id == "succeeded-task").Status
            .Should().BeNull("succeeded tasks whose files were rolled back must be re-executed");
        reconciled.LastTaskFingerprint.Should().BeNull();
        var persisted = await store.LoadAsync(run.Id, TestContext.Current.CancellationToken);
        persisted!.TaskGraph!.Tasks.Single().Status.Should().BeNull();
        persisted.Version.Should().Be(run.Version + 1);
    }

    [Fact]
    public async Task Reconcile_FingerprintMatch_KeepsSucceededTasks()
    {
        var workspace = Path.Combine(_root, "workspace");
        await File.WriteAllTextAsync(
            Path.Combine(workspace, "code.cs"), "v1", TestContext.Current.CancellationToken);
        var provider = new WorkspaceFingerprintProvider();
        var expected = await provider.ComputeAsync(workspace, TestContext.Current.CancellationToken);

        var store = new JsonTeamRunStore(Path.Combine(_root, "runs-match"));
        var run = await CreateClaimedRunAsync(store, "succeeded-task", expected);

        var service = CreateRunService(store, provider);
        var reconciled = await service.ReconcileSucceededTasksAsync(run, TestContext.Current.CancellationToken);

        reconciled.Version.Should().Be(run.Version, "an intact workspace must not trigger a save");
        reconciled.TaskGraph!.Tasks.Single().Status.Should().Be(TeamTaskStatus.Succeeded);
    }

    [Fact]
    public async Task Reconcile_WithoutProvider_IsNoOp()
    {
        var store = new JsonTeamRunStore(Path.Combine(_root, "runs-noprovider"));
        var run = await CreateClaimedRunAsync(store, "succeeded-task", LastTaskFingerprint: "stale");

        var service = CreateRunService(store, fingerprintProvider: null);
        var reconciled = await service.ReconcileSucceededTasksAsync(run, TestContext.Current.CancellationToken);

        reconciled.Should().BeSameAs(run);
        reconciled.TaskGraph!.Tasks.Single().Status.Should().Be(TeamTaskStatus.Succeeded);
    }

    // --- H1: CancelAsync ---

    [Fact]
    public async Task Cancel_PersistsCancelledTerminalWithUncommittedTaskDemotion()
    {
        var store = new JsonTeamRunStore(Path.Combine(_root, "runs-cancel"));
        var run = await CreateClaimedRunAsync(store, "succeeded-task", LastTaskFingerprint: "fingerprint");

        var service = CreateRunService(store, fingerprintProvider: null);
        var cancelled = await service.CancelAsync(
            run.Id, "Team execution was cancelled by the user.", TestContext.Current.CancellationToken);

        cancelled.Status.Should().Be(TeamRunStatus.Cancelled);
        cancelled.Phase.Should().Be(TeamRunPhase.Completed);
        cancelled.TaskGraph!.Tasks.Single(task => task.Definition.Id == "succeeded-task").Status
            .Should().Be(TeamTaskStatus.Succeeded, "completed work stays as history; delivery records committed:false");
        cancelled.Delivery.Should().NotBeNull();
        cancelled.Delivery!.Committed.Should().BeFalse();

        var persisted = await store.LoadAsync(run.Id, TestContext.Current.CancellationToken);
        persisted!.Status.Should().Be(TeamRunStatus.Cancelled);
        var active = await store.ListActiveAsync(TestContext.Current.CancellationToken);
        active.Should().NotContain(persistedRun => persistedRun.Id == run.Id,
            "a cancelled terminal run must no longer appear as resumable");
    }

    [Fact]
    public async Task Cancel_WithPendingTask_DemotesItToCancelled()
    {
        var store = new JsonTeamRunStore(Path.Combine(_root, "runs-cancel-pending"));
        var taskDef = TaskDef("running-task");
        var now = DateTimeOffset.UtcNow;
        var run = new TeamRun
        {
            Id = TeamRunId.NewId(),
            TeamName = "team-a",
            OriginalRequest = "request",
            WorkingDirectory = Path.Combine(_root, "workspace"),
            Phase = TeamRunPhase.Execution,
            Status = TeamRunStatus.Running,
            Plan = new ImplementationPlan("plan", [taskDef], [], [], []),
            TaskGraph = new TeamTaskGraph([new TeamTaskState(taskDef, Status: null)]),
            PlanApproved = true,
            Version = 1,
            CreatedAt = now,
            UpdatedAt = now,
        };
        (await store.TrySaveAsync(run, 0, TestContext.Current.CancellationToken)).Should().BeTrue();

        var service = CreateRunService(store, fingerprintProvider: null);
        var cancelled = await service.CancelAsync(run.Id, "cancelled", TestContext.Current.CancellationToken);

        cancelled.TaskGraph!.Tasks.Single().Status.Should().Be(TeamTaskStatus.Cancelled);
    }

    [Fact]
    public async Task Cancel_TerminalRun_IsIdempotent()
    {
        var store = new JsonTeamRunStore(Path.Combine(_root, "runs-cancel-idempotent"));
        var run = await CreateClaimedRunAsync(store, "succeeded-task", LastTaskFingerprint: null);
        var service = CreateRunService(store, fingerprintProvider: null);
        var first = await service.CancelAsync(run.Id, "cancelled", TestContext.Current.CancellationToken);

        var second = await service.CancelAsync(run.Id, "cancelled again", TestContext.Current.CancellationToken);

        second.Version.Should().Be(first.Version, "cancelling a terminal run must not bump the version");
    }

    // --- H1: 取消的 UI 投影（OrchestrationStreamService 不再把 OCE 转成 Error 事件）---

    [Fact]
    public async Task StreamResumeTeam_UserCancellation_EmitsNoErrorEvent()
    {
        var teamService = Substitute.For<OneCode.Core.Coordinator.ITeamOrchestrationService>();
        teamService.ResumeTeamStreamingAsync(
                Arg.Any<SessionId>(), Arg.Any<Action<OrchestrationEvent>>(), Arg.Any<CancellationToken>())
            .Returns<TeamRunResult>(callInfo => throw new OperationCanceledException());
        var service = CreateOrchestrationStreamService();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var events = new List<TuiEvent>();
        var act = async () =>
        {
            await foreach (var evt in service.StreamResumeTeamAsync(
                teamService, SessionId.NewId(), cts.Token).ConfigureAwait(false))
            {
                events.Add(evt);
            }
        };

        await act.Should().ThrowAsync<OperationCanceledException>();
        events.OfType<TuiError>().Should().BeEmpty(
            "user cancellation must surface as (cancelled) via OCE, not as an error event");
    }

    private static TeamRunApplicationService CreateRunService(
        JsonTeamRunStore store,
        IWorkspaceFingerprintProvider? fingerprintProvider)
        => new(
            store,
            new TeamRunStateMachine(),
            new TeamQualityGateRunner([]),
            new DeliveryReportBuilder(),
            fingerprintProvider);

    private OrchestrationStreamService CreateOrchestrationStreamService()
    {
        var configManager = Substitute.For<IConfigManager>();
        var toolCatalog = Substitute.For<IToolCatalog>();
        var workingDir = Substitute.For<OneCode.Core.Tools.IWorkingDirectoryAccessor>();
        var goalAppService = Substitute.For<IGoalRunApplicationService>();
        var goalHost = new GoalWorkflowHost(
            Substitute.For<IDurableWorkflowHost>(),
            new GoalWorkflowCompiler(),
            Substitute.For<IGoalRunStore>(),
            Substitute.For<IWorkflowRunRegistry>());
        var runtimeFactory = Substitute.For<IGoalWorkflowRuntimeFactory>();
        return new OrchestrationStreamService(
            configManager, toolCatalog, workingDir, goalAppService, goalHost, runtimeFactory);
    }

    private async Task<TeamRun> CreateClaimedRunAsync(
        JsonTeamRunStore store,
        string succeededTaskId,
        string? LastTaskFingerprint)
    {
        var taskDef = TaskDef(succeededTaskId);
        var now = DateTimeOffset.UtcNow;
        var run = new TeamRun
        {
            Id = TeamRunId.NewId(),
            TeamName = "team-a",
            OriginalRequest = "request",
            WorkingDirectory = Path.Combine(_root, "workspace"),
            Phase = TeamRunPhase.Execution,
            Status = TeamRunStatus.Running,
            Plan = new ImplementationPlan("plan", [taskDef], [], [], []),
            TaskGraph = new TeamTaskGraph(
                [new TeamTaskState(taskDef, TeamTaskStatus.Succeeded, Summary: "done")]),
            PlanApproved = true,
            LastTaskFingerprint = LastTaskFingerprint,
            Version = 1,
            CreatedAt = now,
            UpdatedAt = now,
        };
        (await store.TrySaveAsync(run, 0, TestContext.Current.CancellationToken)).Should().BeTrue();
        var saved = await store.LoadAsync(run.Id, TestContext.Current.CancellationToken);
        return await store.ClaimWorkflowAsync(run.Id, 1, saved!.Version, TestContext.Current.CancellationToken);
    }

    private static TeamTaskDefinition TaskDef(string id)
        => new(
            id,
            $"title-{id}",
            TeamTaskKind.Implementation,
            "executor",
            [],
            ["criterion"],
            TeamToolPolicy.WriteAllowed);
}
