using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using OneCode.App.Services.PlanMode;
using OneCode.App.Session;
using OneCode.Core.Build;
using OneCode.Core.Goals;
using OneCode.Core.PlanMode;
using OneCode.Core.Session;
using OneCode.Core.Tokens;
using OneCode.Infrastructure.Api;
using OneCode.Infrastructure.Build;
using OneCode.Infrastructure.Goals;
using OneCode.Infrastructure.Persistence;

namespace OneCode.Tests;

/// <summary>
/// /session delete 级联清理的契约测试：写入端与删除端都用真实 store 实现
/// （临时根目录），覆盖「六类落盘全部消失」「单步失败不阻断其余」「worktree 只提示不删除」。
/// </summary>
public sealed class SessionDataCleanerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "onecode-cleaner-" + Guid.NewGuid().ToString("N"));

    public SessionDataCleanerTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task RemoveAsync_DeletesEverySessionScopedStore_AndKeepsWorktreeAsLeftover()
    {
        var ct = TestContext.Current.CancellationToken;
        var sessionId = SessionId.NewId();
        var fixtures = await SeedSessionDataAsync(sessionId, ct);

        var result = await fixtures.Cleaner.RemoveAsync(sessionId, ct);

        result.FailedSteps.Should().BeEmpty();
        result.LeftoverPaths.Should().Contain(fixtures.IsolatedPath,
            "GOAL 隔离 worktree 可能含未合并改动，只能作为残留提示");

        (await fixtures.SessionStore.LoadAsync(sessionId, ct)).Should().BeNull("事件日志必须删除");
        (await fixtures.PlanStore.LoadAsync(sessionId, ct)).Should().BeNull("计划聚合必须删除");
        (await fixtures.BuildStore.LoadAsync(sessionId, ct)).Should().BeNull("BuildRun 必须删除");
        (await fixtures.GoalStore.LoadBySessionAsync(sessionId, ct)).Should().BeNull("GoalRun 必须删除");
        fixtures.Ledger.GetSessionUsage(sessionId).Should().BeNull("内存态用量桶必须清空");
        Directory.Exists(fixtures.IsolatedPath).Should().BeTrue("级联删除不得自动移除 worktree 目录");
    }

    [Fact]
    public async Task RemoveAsync_StepFails_StillCleansRemainingStepsAndReportsFailure()
    {
        // 反证锚点：去掉分步 try/catch 后第 5 步抛错会中断，goal run 残留且结果里拿不到失败项。
        var ct = TestContext.Current.CancellationToken;
        var sessionId = SessionId.NewId();
        var fixtures = await SeedSessionDataAsync(sessionId, ct);
        var brokenBuildStore = Substitute.For<IBuildRunStore>();
        brokenBuildStore.RemoveSessionAsync(Arg.Any<SessionId>(), Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new IOException("simulated disk failure"));

        var cleaner = new SessionDataCleaner(
            fixtures.EventStore,
            fixtures.SummaryIndex,
            fixtures.Ledger,
            fixtures.PlanStore,
            brokenBuildStore,
            fixtures.GoalStore,
            NullLogger<SessionDataCleaner>.Instance);

        var result = await cleaner.RemoveAsync(sessionId, ct);

        result.FailedSteps.Should().ContainSingle().Which.Should().Be("build runs");
        (await fixtures.GoalStore.LoadBySessionAsync(sessionId, ct)).Should().BeNull(
            "单步失败不得阻断后续步骤");
        (await fixtures.SessionStore.LoadAsync(sessionId, ct)).Should().BeNull();
    }

    [Fact]
    public async Task RemoveAsync_WorktreeInspectionFails_ReportsUnknownLeftovers()
    {
        var ct = TestContext.Current.CancellationToken;
        var sessionId = SessionId.NewId();
        var fixtures = await SeedSessionDataAsync(sessionId, ct);
        var brokenGoalStore = Substitute.For<IGoalRunStore>();
        brokenGoalStore.LoadBySessionAsync(Arg.Any<SessionId>(), Arg.Any<CancellationToken>())
            .Returns<Task<GoalRun?>>(_ => throw new IOException("simulated read failure"));
        var cleaner = new SessionDataCleaner(
            fixtures.EventStore,
            fixtures.SummaryIndex,
            fixtures.Ledger,
            fixtures.PlanStore,
            fixtures.BuildStore,
            brokenGoalStore,
            NullLogger<SessionDataCleaner>.Instance);

        var result = await cleaner.RemoveAsync(sessionId, ct);

        result.FailedSteps.Should().Contain("goal worktree inspection");
    }

    [Fact]
    public async Task RemoveAsync_LateAggregateAndUsageWrites_AreRejected()
    {
        var ct = TestContext.Current.CancellationToken;
        var sessionId = SessionId.NewId();
        var fixtures = await SeedSessionDataAsync(sessionId, ct);
        await fixtures.Cleaner.RemoveAsync(sessionId, ct);
        var now = DateTimeOffset.UtcNow;
        var workflow = PlanWorkflow.Create(sessionId) with { LatestRevision = 1 };
        var aggregate = new PlanAggregate(workflow,
        [
            new PlanRevision
            {
                PlanId = workflow.Id,
                SessionId = sessionId,
                Revision = 1,
                Title = "late",
                Markdown = "# late",
                Steps = [],
                Risks = [],
                Assumptions = [],
                ContentHash = "hash",
                Status = PlanRevisionStatus.Submitted,
                CreatedAt = now,
            },
        ]);
        var buildRun = new BuildRun
        {
            Id = BuildRunId.New(),
            ConversationId = sessionId,
            State = BuildRunState.Created,
            CreatedAt = now,
        };
        var goalRun = new GoalRun
        {
            Id = GoalRunId.New(),
            SessionId = sessionId,
            Goal = "late",
            WorkingDirectory = _root,
            WorkspaceFingerprint = "fp",
            DefinitionHash = "hash",
            Workspace = new GoalWorkspaceSnapshot(
                "ws", _root, fixtures.IsolatedPath, "goal-branch", "main", "commit", "fp", now),
            CreatedAt = now,
        };

        Func<Task> writePlan = () => fixtures.PlanStore.SaveAsync(aggregate, expectedVersion: -1, ct);
        Func<Task> writeBuild = () => fixtures.BuildStore.SaveAsync(buildRun, expectedVersion: 0, ct);
        Func<Task> writeGoal = () => fixtures.GoalStore.SaveAsync(goalRun, expectedVersion: 0, ct);
        var writeUsage = () => fixtures.Ledger.RecordUsage(sessionId, new UsageRecord("model", 1, 1));

        await writePlan.Should().ThrowAsync<InvalidOperationException>().WithMessage("*deleted*");
        await writeBuild.Should().ThrowAsync<InvalidOperationException>().WithMessage("*deleted*");
        await writeGoal.Should().ThrowAsync<InvalidOperationException>().WithMessage("*deleted*");
        writeUsage.Should().Throw<InvalidOperationException>().WithMessage("*deleted*");
    }

    private async Task<CleanerFixtures> SeedSessionDataAsync(SessionId sessionId, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var eventStore = new FileSessionEventStore(_root);
        var summaryIndex = new FileSessionSummaryIndex(_root, NullLogger<FileSessionSummaryIndex>.Instance);
        var sessionStore = new EventSourcedSessionStore(
            eventStore, summaryIndex, NullLogger<EventSourcedSessionStore>.Instance);

        var conversation = new Conversation
        {
            Id = sessionId,
            Name = "cleaner-fixture",
            WorkingDirectory = _root,
            Model = "test-model",
            Status = ConversationStatus.Active,
            CreatedAt = now,
            LastActivityAt = now,
        };
        conversation.Messages.Add(new UserMessage("u1", "hello", now));
        await sessionStore.SaveAsync(conversation, ct);

        // Plan 聚合：LatestRevision 必须等于 revision 数量（ValidateAggregate 不变式）。
        var planStore = new PlanAggregateStore(_root);
        var workflow = PlanWorkflow.Create(sessionId) with { LatestRevision = 1 };
        await planStore.SaveAsync(
            new PlanAggregate(workflow,
            [
                new PlanRevision
                {
                    PlanId = workflow.Id,
                    SessionId = sessionId,
                    Revision = 1,
                    Title = "plan",
                    Markdown = "# plan",
                    Steps = [],
                    Risks = [],
                    Assumptions = [],
                    ContentHash = "hash",
                    Status = PlanRevisionStatus.Submitted,
                    CreatedAt = now,
                },
            ]),
            expectedVersion: -1,
            ct);

        var buildStore = new JsonBuildRunStore(_root);
        await buildStore.SaveAsync(
            new BuildRun
            {
                Id = BuildRunId.New(),
                ConversationId = sessionId,
                State = BuildRunState.Created,
                CreatedAt = now,
            },
            expectedVersion: 0,
            ct);

        var isolatedPath = Path.Combine(_root, "goal-worktree");
        Directory.CreateDirectory(isolatedPath);
        var goalStore = new JsonGoalRunStore(Path.Combine(_root, "goal-runs"));
        await goalStore.SaveAsync(
            new GoalRun
            {
                Id = GoalRunId.New(),
                SessionId = sessionId,
                Goal = "goal",
                WorkingDirectory = _root,
                WorkspaceFingerprint = "fp",
                DefinitionHash = "hash",
                Workspace = new GoalWorkspaceSnapshot(
                    "ws", _root, isolatedPath, "goal-branch", "main", "commit", "fp", now),
                CreatedAt = now,
            },
            expectedVersion: 0,
            ct);

        var ledger = new TokenLedger(new JsonUsageStateStore(_root));
        ledger.RecordUsage(sessionId, new UsageRecord("test-model", 10, 5));

        var cleaner = new SessionDataCleaner(
            eventStore,
            summaryIndex,
            ledger,
            planStore,
            buildStore,
            goalStore,
            NullLogger<SessionDataCleaner>.Instance);

        return new CleanerFixtures(
            eventStore, summaryIndex, sessionStore, planStore, buildStore, goalStore, ledger, cleaner, isolatedPath);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private sealed record CleanerFixtures(
        ISessionEventStore EventStore,
        ISessionSummaryIndex SummaryIndex,
        EventSourcedSessionStore SessionStore,
        PlanAggregateStore PlanStore,
        JsonBuildRunStore BuildStore,
        JsonGoalRunStore GoalStore,
        TokenLedger Ledger,
        SessionDataCleaner Cleaner,
        string IsolatedPath);
}
