using NSubstitute;
using OneCode.App.Commands;
using OneCode.Core.Commands;
using OneCode.Core.Coordinator;
using OneCode.Core.Domain;
using OneCode.Core.Goals;

namespace OneCode.Tests;

/// <summary>
/// /checkpoint resume 子命令端到端验证。
///
/// 测试覆盖：
/// 1. /checkpoint resume（无参数）列出所有可恢复会话（Goal + Team 混合）
/// 2. /checkpoint resume &lt;sessionId&gt; 自动判断 Goal/Team 类型并返回 ResumeWorkflowResult
/// 3. 未找到 sessionId 时返回错误并提示可用会话
/// 4. Goal/Team store 为 null 时的降级行为
/// 5. ResumeWorkflowResult 携带正确的 SessionId 和 WorkflowResumeKind
/// 6. TuiDone.SessionId / TeamRunResult.SessionId 传递
/// 7. resume 子命令不需要活跃会话（与 save/list/restore/delete 不同）
/// </summary>
public sealed class CheckpointResumeIntegrationTests
{
    // 无参数：列出所有可恢复会话

    [Fact]
    public async Task Resume_NoArgs_ListsAllResumableSessions()
    {
        var goalStore = await CreateGoalStoreWithSessions("aaaa1111bbbb2222cccc3333dddd4444");
        var teamStore = CreateTeamStoreWithSessions("team-session-1");
        var cmd = CreateCommand(goalStore, teamStore);

        var result = await cmd.ExecuteAsync(new[] { "resume" }, TestContext.Current.CancellationToken);

        var text = AssertTextResult(result);
        text.Should().Contain("Resumable tasks");
        text.Should().Contain("aaaa1111bbbb2222cccc3333dddd4444");
        text.Should().Contain("team-session-1");
        text.Should().Contain("/checkpoint resume <sessionId>");
    }

    [Fact]
    public async Task Resume_NoArgs_NoSessions_ShowsHelpMessage()
    {
        var goalStore = await CreateGoalStoreWithSessions();
        var teamStore = CreateTeamStoreWithSessions();
        var cmd = CreateCommand(goalStore, teamStore);

        var result = await cmd.ExecuteAsync(new[] { "resume" }, TestContext.Current.CancellationToken);

        var text = AssertTextResult(result);
        text.Should().Contain("No interrupted tasks to resume");
        text.Should().Contain("Goal runs use the durable Workflow Registry");
    }

    [Fact]
    public async Task Resume_NoArgs_BothStoresNull_ShowsHelpMessage()
    {
        var cmd = CreateCommand(goalRunStore: null, teamRunStore: null);

        var result = await cmd.ExecuteAsync(new[] { "resume" }, TestContext.Current.CancellationToken);

        var text = AssertTextResult(result);
        text.Should().Contain("No interrupted tasks to resume");
    }

    // 有参数：自动判断类型

    [Fact]
    public async Task Resume_GoalSessionId_ReturnsResumeWorkflowResult()
    {
        var sessionId = "abc123def456abcd7890abcd1234abcd";
        var goalStore = await CreateGoalStoreWithSessions(sessionId);
        var cmd = CreateCommand(goalStore, teamRunStore: null);

        var result = await cmd.ExecuteAsync(new[] { "resume", sessionId }, TestContext.Current.CancellationToken);

        var resume = AssertResumeWorkflowResult(result);
        resume.SessionId.Should().Be(sessionId);
        resume.Kind.Should().Be(WorkflowResumeKind.Goal);
    }

    [Fact]
    public async Task Resume_TeamSessionId_ReturnsResumeWorkflowResult()
    {
        var teamStore = CreateTeamStoreWithSessions("team-xyz789");
        var cmd = CreateCommand(goalRunStore: null, teamRunStore: teamStore);

        var result = await cmd.ExecuteAsync(new[] { "resume", "team-xyz789" }, TestContext.Current.CancellationToken);

        var resume = AssertResumeWorkflowResult(result);
        resume.SessionId.Should().Be("team-xyz789");
        resume.Kind.Should().Be(WorkflowResumeKind.Team);
    }

    [Fact]
    public async Task Resume_SessionIdInBothStores_PrefersGoal()
    {
        var sessionId = "shared1112223334445556667778889990a";
        var goalStore = await CreateGoalStoreWithSessions(sessionId);
        var teamStore = CreateTeamStoreWithSessions(sessionId);
        var cmd = CreateCommand(goalStore, teamStore);

        var result = await cmd.ExecuteAsync(new[] { "resume", sessionId }, TestContext.Current.CancellationToken);

        var resume = AssertResumeWorkflowResult(result);
        resume.Kind.Should().Be(WorkflowResumeKind.Goal);
    }

    [Fact]
    public async Task Resume_GoalStoreNull_TeamSessionId_ReturnsTeamResumeResult()
    {
        var teamStore = CreateTeamStoreWithSessions("team-only");
        var cmd = CreateCommand(goalRunStore: null, teamRunStore: teamStore);

        var result = await cmd.ExecuteAsync(new[] { "resume", "team-only" }, TestContext.Current.CancellationToken);

        var resume = AssertResumeWorkflowResult(result);
        resume.Kind.Should().Be(WorkflowResumeKind.Team);
    }

    [Fact]
    public async Task Resume_TeamServiceNull_GoalSessionId_ReturnsGoalResumeResult()
    {
        var sessionId = "only0000000000000000000000aaaaaaa";
        var goalStore = await CreateGoalStoreWithSessions(sessionId);
        var cmd = CreateCommand(goalStore, teamRunStore: null);

        var result = await cmd.ExecuteAsync(new[] { "resume", sessionId }, TestContext.Current.CancellationToken);

        var resume = AssertResumeWorkflowResult(result);
        resume.Kind.Should().Be(WorkflowResumeKind.Goal);
    }

    // 未找到 sessionId

    [Fact]
    public async Task Resume_NonExistentSession_ReturnsErrorWithAvailableList()
    {
        var existingId = "aaa111bbb222ccc333ddd444eee555ff";
        var goalStore = await CreateGoalStoreWithSessions(existingId);
        var teamStore = CreateTeamStoreWithSessions("team-exists");
        var cmd = CreateCommand(goalStore, teamStore);

        var result = await cmd.ExecuteAsync(new[] { "resume", "goal-nonexistent000000000000000000dead" }, TestContext.Current.CancellationToken);

        var error = AssertErrorResult(result);
        error.Should().Contain("not found");
        error.Should().Contain("Available sessions");
        error.Should().Contain(existingId);
        error.Should().Contain("team-exists");
    }

    [Fact]
    public async Task Resume_NonExistentSession_NoAvailableSessions()
    {
        var goalStore = await CreateGoalStoreWithSessions();
        var teamStore = CreateTeamStoreWithSessions();
        var cmd = CreateCommand(goalStore, teamStore);

        var result = await cmd.ExecuteAsync(new[] { "resume", "goal-nonexistent000000000000000000dead" }, TestContext.Current.CancellationToken);

        var error = AssertErrorResult(result);
        error.Should().Contain("not found");
        error.Should().Contain("No resumable sessions available");
    }

    [Fact]
    public async Task Resume_BothStoresNull_NonExistentSession_ReturnsError()
    {
        var cmd = CreateCommand(goalRunStore: null, teamRunStore: null);

        var result = await cmd.ExecuteAsync(new[] { "resume", "any-session" }, TestContext.Current.CancellationToken);

        var error = AssertErrorResult(result);
        error.Should().Contain("not found");
        error.Should().Contain("No resumable sessions available");
    }

    // resume 子命令不需要活跃会话

    [Fact]
    public async Task Resume_WorksWithoutActiveConversation()
    {
        var sessionId = "goal-noconv000000000000000000000000ff";
        var goalStore = await CreateGoalStoreWithSessions(sessionId);
        var sessionManager = Substitute.For<OneCode.App.Session.ISessionManager>();
        sessionManager.ForegroundConversation.Returns((OneCode.Core.Domain.Conversation?)null);
        var cmd = new CheckpointCommand(sessionManager, goalStore, teamRunStore: null);

        var result = await cmd.ExecuteAsync(new[] { "resume", sessionId }, TestContext.Current.CancellationToken);

        var resume = AssertResumeWorkflowResult(result);
        resume.Kind.Should().Be(WorkflowResumeKind.Goal);
    }

    // Helpers

    private static CheckpointCommand CreateCommand(
        IGoalRunStore? goalRunStore = null,
        ITeamRunStore? teamRunStore = null)
    {
        var sessionManager = Substitute.For<OneCode.App.Session.ISessionManager>();
        sessionManager.ForegroundConversation.Returns((OneCode.Core.Domain.Conversation?)null);
        return new CheckpointCommand(sessionManager, goalRunStore, teamRunStore);
    }

    private static Task<IGoalRunStore> CreateGoalStoreWithSessions(params string[] sessionIds)
    {
        var runs = sessionIds.Select((sid, index) => new GoalRun
        {
            Id = new GoalRunId($"goal-{index}"),
            SessionId = new SessionId(sid),
            Goal = "Test",
            WorkingDirectory = Path.GetTempPath(),
            WorkspaceFingerprint = "fingerprint",
            DefinitionHash = "definition",
            State = GoalRunState.Paused,
            Plan =
            [
                new GoalStepSnapshot(1, "Test", "done", GoalStepState.Pending, [], 0, false, [], [], false, false, false),
            ],
        }).ToArray();
        var store = Substitute.For<IGoalRunStore>();
        store.ListActiveAsync(Arg.Any<CancellationToken>()).Returns(runs);
        store.LoadBySessionAsync(Arg.Any<SessionId>(), Arg.Any<CancellationToken>())
            .Returns(call => runs.SingleOrDefault(run => run.SessionId == call.ArgAt<SessionId>(0)));
        return Task.FromResult(store);
    }

    private static ITeamRunStore CreateTeamStoreWithSessions(params string[] sessionIds)
    {
        var store = Substitute.For<ITeamRunStore>();
        var now = DateTimeOffset.UtcNow;
        var runs = sessionIds.Select((sessionId, index) => new TeamRun
        {
            Id = new TeamRunId($"team-{index}"),
            TeamName = "test-team",
            OriginalRequest = "test",
            WorkingDirectory = Environment.CurrentDirectory,
            Phase = TeamRunPhase.Execution,
            Status = TeamRunStatus.Running,
            SessionId = new SessionId(sessionId),
            Version = 1,
            CreatedAt = now,
            UpdatedAt = now,
        }).ToArray();
        store.ListActiveAsync(Arg.Any<CancellationToken>())
            .Returns(runs);
        return store;
    }

    private static string AssertTextResult(CommandResult result)
    {
        result.Should().BeOfType<CommandResult.TextResult>();
        return ((CommandResult.TextResult)result).Value;
    }

    private static CommandResult.ResumeWorkflowResult AssertResumeWorkflowResult(CommandResult result)
    {
        result.Should().BeOfType<CommandResult.ResumeWorkflowResult>();
        return (CommandResult.ResumeWorkflowResult)result;
    }

    private static string AssertErrorResult(CommandResult result)
    {
        result.Should().BeOfType<CommandResult.ErrorResult>();
        return ((CommandResult.ErrorResult)result).Message;
    }
}
