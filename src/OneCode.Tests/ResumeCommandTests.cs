using NSubstitute;
using OneCode.App.Commands;
using OneCode.App.Session;
using OneCode.Core.Commands;
using OneCode.Core.Coordinator;
using OneCode.Core.Domain;
using OneCode.Core.Goals;

namespace OneCode.Tests;

/// <summary>
/// /resume 命令端到端验证（自 CheckpointCommand 拆出）。
///
/// 测试覆盖：
/// 1. /resume（无参数）列出所有可恢复会话（Goal + Team 混合）
/// 2. /resume &lt;sessionId&gt; 自动判断 Goal/Team 类型并返回 ResumeWorkflowResult
/// 3. 未找到 sessionId 时返回错误并提示可用会话
/// 4. Goal/Team 同名会话时 Goal 优先
/// 5. ResumeWorkflowResult 携带正确的 SessionId 和 WorkflowResumeKind
/// 6. /checkpoint resume 寄居分支已删除（回归守卫）
/// </summary>
public sealed class ResumeCommandTests
{
    // 无参数：列出所有可恢复会话

    [Fact]
    public async Task Resume_NoArgs_ListsAllResumableSessions()
    {
        var goalStore = await CreateGoalStoreWithSessions("aaaa1111bbbb2222cccc3333dddd4444");
        var teamStore = CreateTeamStoreWithSessions("team-session-1");
        var cmd = new ResumeCommand(goalStore, teamStore);

        var result = await cmd.ExecuteAsync([], TestContext.Current.CancellationToken);

        var text = AssertTextResult(result);
        text.Should().Contain("Resumable tasks");
        text.Should().Contain("aaaa1111bbbb2222cccc3333dddd4444");
        text.Should().Contain("team-session-1");
        text.Should().Contain("/resume <sessionId>");
    }

    [Fact]
    public async Task Resume_NoArgs_NoSessions_ShowsHelpMessage()
    {
        var goalStore = await CreateGoalStoreWithSessions();
        var teamStore = CreateTeamStoreWithSessions();
        var cmd = new ResumeCommand(goalStore, teamStore);

        var result = await cmd.ExecuteAsync([], TestContext.Current.CancellationToken);

        var text = AssertTextResult(result);
        text.Should().Contain("No interrupted tasks to resume");
        text.Should().Contain("Goal runs use the durable Workflow Registry");
    }

    [Fact]
    public async Task Resume_NoArgs_BothStoresEmpty_ShowsHelpMessage()
    {
        var cmd = new ResumeCommand(await CreateGoalStoreWithSessions(), CreateTeamStoreWithSessions());

        var result = await cmd.ExecuteAsync([], TestContext.Current.CancellationToken);

        var text = AssertTextResult(result);
        text.Should().Contain("No interrupted tasks to resume");
    }

    // 有参数：自动判断类型

    [Fact]
    public async Task Resume_GoalSessionId_ReturnsResumeWorkflowResult()
    {
        var sessionId = "abc123def456abcd7890abcd1234abcd";
        var goalStore = await CreateGoalStoreWithSessions(sessionId);
        var cmd = new ResumeCommand(goalStore, CreateTeamStoreWithSessions());

        var result = await cmd.ExecuteAsync([sessionId], TestContext.Current.CancellationToken);

        var resume = AssertResumeWorkflowResult(result);
        resume.SessionId.Should().Be(sessionId);
        resume.Kind.Should().Be(WorkflowResumeKind.Goal);
    }

    [Fact]
    public async Task Resume_TeamSessionId_ReturnsResumeWorkflowResult()
    {
        var teamStore = CreateTeamStoreWithSessions("team-xyz789");
        var cmd = new ResumeCommand(await CreateGoalStoreWithSessions(), teamStore);

        var result = await cmd.ExecuteAsync(["team-xyz789"], TestContext.Current.CancellationToken);

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
        var cmd = new ResumeCommand(goalStore, teamStore);

        var result = await cmd.ExecuteAsync([sessionId], TestContext.Current.CancellationToken);

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
        var cmd = new ResumeCommand(goalStore, teamStore);

        var result = await cmd.ExecuteAsync(["goal-nonexistent000000000000000000dead"], TestContext.Current.CancellationToken);

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
        var cmd = new ResumeCommand(goalStore, teamStore);

        var result = await cmd.ExecuteAsync(["goal-nonexistent000000000000000000dead"], TestContext.Current.CancellationToken);

        var error = AssertErrorResult(result);
        error.Should().Contain("not found");
        error.Should().Contain("No resumable sessions available");
    }

    [Fact]
    public async Task Resume_BothStoresEmpty_NonExistentSession_ReturnsError()
    {
        var cmd = new ResumeCommand(await CreateGoalStoreWithSessions(), CreateTeamStoreWithSessions());

        var result = await cmd.ExecuteAsync(["any-session"], TestContext.Current.CancellationToken);

        var error = AssertErrorResult(result);
        error.Should().Contain("not found");
        error.Should().Contain("No resumable sessions available");
    }

    // /checkpoint resume 寄居分支已删除（回归守卫）

    [Fact]
    public async Task Checkpoint_ResumeSubcommand_ReturnsError()
    {
        // 必须有活跃会话，否则会话判空先拦截，测不到子命令分支
        var conv = new Conversation { Name = "active", WorkingDirectory = Path.GetTempPath() };
        var sessionManager = Substitute.For<ISessionManager>();
        sessionManager.ForegroundConversation.Returns(conv);
        var cmd = new CheckpointCommand(sessionManager);

        var result = await cmd.ExecuteAsync(["resume", "any-session"], TestContext.Current.CancellationToken);

        // resume 不再寄居在 /checkpoint 下：应落入未知子命令错误，而非返回 ResumeWorkflowResult
        AssertErrorResult(result).Should().Contain("Usage");
        result.Should().NotBeOfType<CommandResult.ResumeWorkflowResult>();
    }

    // Helpers

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
