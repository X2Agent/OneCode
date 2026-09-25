using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using OneCode.App.Commands;
using OneCode.App.Query;
using OneCode.App.Services;
using OneCode.App.Session;
using OneCode.App.Tools;
using OneCode.Core.Commands;
using OneCode.Core.Domain;
using OneCode.App.Services.Observability;
using OneCode.App.Services.GoalMode;
using OneCode.Core.Goals;
using OneCode.Infrastructure.Api;

namespace OneCode.Tests;

public sealed class StatusCommandStatsTests
{
    [Fact]
    public async Task Stats_WithTokenUsageTracker_ShowsCacheHitRateAndBreakdown()
    {
        var sessionManager = CreateSessionManager();
        var appState = Substitute.For<IAppStateAccessor>();
        var modeProvider = new PermissionModeProvider(TestSupport.TestConfigManager.Create());
        var tracker = new TokenUsageTracker(new TokenLedger(), TestSupport.NullSessionIdProvider.Instance);

        // 记录一次：InputTokens=400 (完整输入，含缓存命中), CacheReadTokens=300, CacheWriteTokens=50, OutputTokens=200
        tracker.Record(
            new TokenUsage(400, 200, CacheReadTokens: 300, CacheWriteTokens: 50),
            new TokenBreakdown(
                SystemPrompt: 50,
                ToolsAndSkills: 80,
                Messages: 70,
                Other: 100,
                TotalEstimated: 300,
                SystemPromptDetail: new SystemPromptBreakdown(
                    TemplateBody: 20, Environment: 10, ProjectContext: 15, Memory: 5, OtherSections: 0)));

        var sut = new StatusCommand(sessionManager, appState, modeProvider, tracker, modelManager: null!, goalRunService: Substitute.For<IGoalRunApplicationService>());

        var result = await sut.ExecuteAsync(new[] { "stats" }, TestContext.Current.CancellationToken);

        result.Should().BeOfType<CommandResult.TextResult>();
        var text = ((CommandResult.TextResult)result).Value;

        text.Should().Contain("LLM queries:     1");
        text.Should().Contain("Input tokens:    400");
        text.Should().Contain("Output tokens:   200");
        text.Should().Contain("Cache read:      300");
        text.Should().Contain("hit rate"); // 缓存命中率
        text.Should().Contain("Cache write:     50");
        text.Should().Contain("Token Breakdown");
        text.Should().Contain("System prompt:  50");
        text.Should().Contain("Tools & skills: 80");
        text.Should().Contain("Messages:       70");
        text.Should().Contain("Other context:  100");
        // System prompt 细分
        text.Should().Contain("Template:");
        text.Should().Contain("Environment:");
        text.Should().Contain("Project ctx:");
        text.Should().Contain("Memory:");
    }

    [Fact]
    public async Task Stats_WithoutTokenUsageTracker_FallsBackToConversationUsage()
    {
        var sessionManager = CreateSessionManager();
        var appState = Substitute.For<IAppStateAccessor>();
        var modeProvider = new PermissionModeProvider(TestSupport.TestConfigManager.Create());
        var sut = new StatusCommand(sessionManager, appState, modeProvider, tokenUsageTracker: null!, modelManager: null!, goalRunService: Substitute.For<IGoalRunApplicationService>());

        var result = await sut.ExecuteAsync(new[] { "stats" }, TestContext.Current.CancellationToken);

        result.Should().BeOfType<CommandResult.TextResult>();
        var text = ((CommandResult.TextResult)result).Value;

        // 无 tracker 时不显示 LLM queries 和 breakdown
        text.Should().NotContain("LLM queries");
        text.Should().NotContain("Token Breakdown");
        // 但仍显示基本统计
        text.Should().Contain("Input tokens");
        text.Should().Contain("Output tokens");
    }

    [Fact]
    public async Task Info_WithGoalWorkspace_ShowsWorktreePath()
    {
        var sessionManager = CreateSessionManager();
        await sessionManager.CreateAsync(new ConversationOptions(
            WorkingDirectory: Path.GetTempPath()), TestContext.Current.CancellationToken);
        var conv = sessionManager.ForegroundConversation!;

        var appState = Substitute.For<IAppStateAccessor>();
        appState.Current.Returns(new AppState());
        var modeProvider = new PermissionModeProvider(TestSupport.TestConfigManager.Create());
        var goalRunService = Substitute.For<IGoalRunApplicationService>();
        goalRunService.GetBySessionAsync(conv.Id, Arg.Any<CancellationToken>())
            .Returns(new GoalRun
            {
                Id = new GoalRunId(Guid.NewGuid().ToString("N")),
                SessionId = conv.Id,
                Goal = "test goal",
                WorkingDirectory = Path.GetTempPath(),
                WorkspaceFingerprint = "fp",
                DefinitionHash = "hash",
                Workspace = new GoalWorkspaceSnapshot(
                    WorkspaceId: "ws",
                    RepositoryRoot: "C:/repo",
                    IsolatedPath: "C:/repo/.onecode/goal-worktrees/run1",
                    WorktreeBranch: "onecode/goal/run1",
                    TargetBranch: "main",
                    BaseCommit: "abc123",
                    TargetWorkspaceFingerprint: "fp2",
                    CreatedAt: DateTimeOffset.UtcNow),
            });

        var sut = new StatusCommand(sessionManager, appState, modeProvider, tokenUsageTracker: null!, modelManager: null!, goalRunService);

        var result = await sut.ExecuteAsync(Array.Empty<string>(), TestContext.Current.CancellationToken);

        var text = ((CommandResult.TextResult)result).Value;
        text.Should().Contain("Goal worktree: C:/repo/.onecode/goal-worktrees/run1");
        text.Should().Contain("onecode/goal/run1");
    }

    [Fact]
    public async Task Info_WithoutGoalRun_OmitsWorktreeLine()
    {
        var sessionManager = CreateSessionManager();
        await sessionManager.CreateAsync(new ConversationOptions(
            WorkingDirectory: Path.GetTempPath()), TestContext.Current.CancellationToken);

        var appState = Substitute.For<IAppStateAccessor>();
        appState.Current.Returns(new AppState());
        var modeProvider = new PermissionModeProvider(TestSupport.TestConfigManager.Create());
        var goalRunService = Substitute.For<IGoalRunApplicationService>();
        goalRunService.GetBySessionAsync(Arg.Any<SessionId>(), Arg.Any<CancellationToken>())
            .Returns((GoalRun?)null);

        var sut = new StatusCommand(sessionManager, appState, modeProvider, tokenUsageTracker: null!, modelManager: null!, goalRunService);

        var result = await sut.ExecuteAsync(Array.Empty<string>(), TestContext.Current.CancellationToken);

        var text = ((CommandResult.TextResult)result).Value;
        text.Should().NotContain("Goal worktree");
    }

    private static SessionManager CreateSessionManager()
    {
        var store = Substitute.For<ISessionStore>();
        return new SessionManager(
            store,
            NullLogger<SessionManager>.Instance,
            Path.GetTempPath(),
            shellExecutorCleanup: Substitute.For<IShellExecutorCleanup>(),
            tokenUsageTracker: Substitute.For<ITokenUsageTracker>(),
            sessionIdHolder: new SessionIdHolder(),
            sessionToolSetManager: Substitute.For<ISessionToolSetManager>());
    }
}
