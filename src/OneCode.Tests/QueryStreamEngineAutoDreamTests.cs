using System.Threading.Channels;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using NSubstitute;
using OneCode.App.Query;
using OneCode.App.Services;
using OneCode.App.Services.Agent;
using OneCode.App.Services.AutoDream;
using OneCode.Infrastructure;
using OneCode.App.Services.Observability;
using OneCode.App.Session;
using OneCode.App.Tools;
using OneCode.Core.Config;
using OneCode.Core.Hooks;
using OneCode.Core.Tools;
using OneCode.Tests.TestSupport;

namespace OneCode.Tests;

/// <summary>
/// 验证 <see cref="QueryStreamEngine"/> 仅在 <see cref="WorkingMode.Build"/> 且任务正常完成未回滚时触发
/// <see cref="IAutoDreamService.NotifyTaskCompleted"/>，<see cref="WorkingMode.Plan"/> 或事务回滚时绝不触发。
/// </summary>
public sealed class QueryStreamEngineAutoDreamTests
{
    [Fact]
    public async Task StreamQueryAsync_BuildModeCompleted_TriggersAutoDreamExactlyOnce_WhereasPlanModeDoesNot()
    {
        var ct = TestContext.Current.CancellationToken;
        var autoDream = Substitute.For<IAutoDreamService>();
        var sut = CreateEngine(autoDream, transactionRolledBack: false);

        // 1. WorkingMode.Plan 完成时不触发 AutoDream
        await foreach (var _ in sut.StreamQueryAsync("draft plan", "sys", "model1", ct: ct, workingMode: WorkingMode.Plan))
        {
        }
        autoDream.DidNotReceive().NotifyTaskCompleted();

        // 2. WorkingMode.Build 正常完成时触发 AutoDream
        await foreach (var _ in sut.StreamQueryAsync("implement feature", "sys", "model1", ct: ct, workingMode: WorkingMode.Build))
        {
        }
        autoDream.Received(1).NotifyTaskCompleted();
    }

    [Fact]
    public async Task StreamQueryAsync_BuildModeRolledBack_DoesNotTriggerAutoDream()
    {
        var ct = TestContext.Current.CancellationToken;
        var autoDream = Substitute.For<IAutoDreamService>();
        var sut = CreateEngine(autoDream, transactionRolledBack: true);

        await foreach (var _ in sut.StreamQueryAsync("broken edit", "sys", "model1", ct: ct, workingMode: WorkingMode.Build))
        {
        }

        autoDream.DidNotReceive().NotifyTaskCompleted();
    }

    private static QueryStreamEngine CreateEngine(IAutoDreamService autoDream, bool transactionRolledBack)
    {
        var toolMetadata = new ToolMetadataRegistry();
        var toolCatalog = new ToolCatalog(new Lazy<List<AIFunction>>(() => []), toolMetadata, mcpConnectionManager: null);
        var configManager = TestConfigManager.Create();
        configManager.Current.Returns(ConfigSnapshot.FromEffective(new AppSettings()));
        var sessionManager = Substitute.For<ISessionManager>();
        sessionManager.WorkingDirectory.Returns(Path.GetTempPath());

        var runner = Substitute.For<IMainAgentRunner>();
        runner.RunStreamingAsync(
                Arg.Any<MainAgentRunOptions>(),
                Arg.Do<ChannelWriter<object>>(writer =>
                {
                    writer.TryWrite(new AgentResponseUpdate { Contents = { new TextContent("done") } });
                    writer.Complete();
                }),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new MainAgentRunResult(
                Text: "done",
                TotalInputTokens: 10,
                TotalOutputTokens: 5,
                TurnCount: 1,
                TransactionRolledBack: transactionRolledBack)));

        return new QueryStreamEngine(
            Substitute.For<ILogger<QueryStreamEngine>>(),
            runner,
            toolCatalog,
            Substitute.For<IHookExecutionService>(),
            new ChatSessionDependencies(
                sessionManager,
                new SessionToolSetManager(toolCatalog, toolMetadata),
                new ToolCapabilityResolver(toolCatalog),
                configManager),
            new ChatObservabilityDependencies(
                Substitute.For<ITokenUsageTracker>(),
                new TokenBreakdownEstimator(TestTokenEstimators.Default),
                Substitute.For<INotifierService>(),
                autoDream));
    }
}
