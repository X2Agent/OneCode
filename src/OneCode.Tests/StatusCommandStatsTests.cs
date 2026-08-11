using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using OneCode.App.Commands;
using OneCode.App.Query;
using OneCode.App.Services;
using OneCode.App.Session;
using OneCode.App.Tools;
using OneCode.Core.Commands;
using OneCode.Core.Domain;
using OneCode.Core.Hooks;
using OneCode.App.Services.Observability;
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
        var costTracker = new CostTracker();
        var tracker = new TokenUsageTracker(new CostTracker(), TestSupport.NullSessionIdProvider.Instance);

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

        var sut = new StatusCommand(sessionManager, appState, modeProvider, costTracker, tracker, modelManager: null!);

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
        var costTracker = new CostTracker();

        var sut = new StatusCommand(sessionManager, appState, modeProvider, costTracker, tokenUsageTracker: null!, modelManager: null!);

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

    private static SessionManager CreateSessionManager()
    {
        var store = Substitute.For<ISessionStore>();
        return new SessionManager(
            store,
            NullLogger<SessionManager>.Instance,
            Path.GetTempPath(),
            hookExecutionService: Substitute.For<IHookExecutionService>(),
            shellExecutorCleanup: Substitute.For<IShellExecutorCleanup>(),
            tokenUsageTracker: Substitute.For<ITokenUsageTracker>(),
            sessionIdHolder: new SessionIdHolder(),
            sessionToolSetManager: Substitute.For<ISessionToolSetManager>());
    }
}
