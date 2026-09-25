using System.Threading.Channels;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using OneCode.App.Query;
using OneCode.App.Services;
using OneCode.App.Services.Agent;
using OneCode.App.Services.Notifier;
using OneCode.App.Services.Observability;
using OneCode.App.Session;
using OneCode.App.Tools;
using OneCode.Core.Config;
using OneCode.Core.Hooks;
using OneCode.Core.Tools;
using OneCode.Tests.TestSupport;

namespace OneCode.Tests;

/// <summary>
/// 覆盖 <see cref="QueryStreamEngine"/> 的 input / output 拦截点门控——
/// 主循环中唯一此前无任何测试的分支（Hook 基础设施另有 HookExecutionServiceTests 覆盖）。
///
/// 契约：
/// - input deny：不进入运行循环、不落会话历史；
/// - output deny：拦截最终响应并终结本轮，**不重入**（纠偏续跑已随 Stop 事件退出 Hook 协议）。
/// </summary>
public sealed class QueryStreamEngineHookGateTests
{
    [Fact]
    public async Task StreamInteractiveAsync_HooksAllowCompletion_RunsOnce()
    {
        var ct = TestContext.Current.CancellationToken;
        var (engine, runner, _) = CreateEngine(textForRound: n => $"round-{n}");

        var events = await CollectAsync(engine, ct);

        await runner.Received(1).RunStreamingAsync(
            Arg.Any<MainAgentRunOptions>(), Arg.Any<ChannelWriter<object>>(), Arg.Any<CancellationToken>());
        events.OfType<ErrorEvent>().Should().BeEmpty();
        events.OfType<DoneEvent>().Should().ContainSingle()
            .Which.FullText.Should().Be("round-1");
    }

    /// <summary>
    /// output deny 必须终结本轮且**不重入**：纠偏续跑不属于该拦截点职责。
    /// 若误改为重入循环，本用例会因运行次数超限而失败。
    /// </summary>
    [Fact]
    public async Task StreamInteractiveAsync_OutputHookBlocks_TerminatesWithoutRetry()
    {
        var ct = TestContext.Current.CancellationToken;
        var (engine, runner, _) = CreateEngine(
            textForRound: n => $"round-{n}",
            hookResult: point => point == HookInterceptionPoint.Output
                ? Blocking("secret in output")
                : new AggregatedHookResult());

        var events = await CollectAsync(engine, ct);

        await runner.Received(1).RunStreamingAsync(
            Arg.Any<MainAgentRunOptions>(), Arg.Any<ChannelWriter<object>>(), Arg.Any<CancellationToken>());

        events.OfType<ErrorEvent>().Should().Contain(e => e.Message.Contains("Response blocked by hook"));
        events.OfType<DoneEvent>().Should().ContainSingle();
    }

    [Fact]
    public async Task StreamInteractiveAsync_InputHookBlocks_SkipsRunEntirely()
    {
        var ct = TestContext.Current.CancellationToken;
        var (engine, runner, _) = CreateEngine(
            textForRound: n => $"round-{n}",
            hookResult: point => point == HookInterceptionPoint.Input
                ? Blocking("prompt rejected")
                : new AggregatedHookResult());

        var events = await CollectAsync(engine, ct);

        await runner.DidNotReceive().RunStreamingAsync(
            Arg.Any<MainAgentRunOptions>(), Arg.Any<ChannelWriter<object>>(), Arg.Any<CancellationToken>());
        events.OfType<ErrorEvent>().Should().ContainSingle()
            .Which.Message.Should().Contain("Prompt blocked by hook");
        events.OfType<DoneEvent>().Should().ContainSingle();
    }

    private static AggregatedHookResult Blocking(string error) =>
        new() { BlockingErrors = [new HookBlockingError(error, "test-hook")] };

    private static async Task<List<QueryEvent>> CollectAsync(QueryStreamEngine engine, CancellationToken ct)
    {
        var events = new List<QueryEvent>();
        await foreach (var evt in engine.StreamInteractiveAsync(
            messages: [new ChatMessage(ChatRole.User, "hello")],
            systemPrompt: "system",
            modelId: "test-model",
            ct: ct))
        {
            events.Add(evt);
        }
        return events;
    }

    private static (QueryStreamEngine Engine, IMainAgentRunner Runner, IHookExecutionService Hooks) CreateEngine(
        Func<int, string>? textForRound = null,
        Func<HookInterceptionPoint, AggregatedHookResult>? hookResult = null)
    {
        var toolMetadata = new ToolMetadataRegistry();
        var toolCatalog = new ToolCatalog(
            new Lazy<List<AIFunction>>(() => []),
            toolMetadata,
            mcpConnectionManager: null);

        var configManager = TestConfigManager.Create();
        configManager.Current.Returns(ConfigSnapshot.FromEffective(new AppSettings { MaxTurns = 100 }));

        var sessionManager = Substitute.For<ISessionManager>();
        sessionManager.WorkingDirectory.Returns(Path.GetTempPath());

        var round = 0;
        var runner = Substitute.For<IMainAgentRunner>();
        runner.RunStreamingAsync(
                Arg.Any<MainAgentRunOptions>(),
                Arg.Do<ChannelWriter<object>>(writer =>
                {
                    round++;
                    if (textForRound?.Invoke(round) is { Length: > 0 } text)
                        writer.TryWrite(new AgentResponseUpdate { Contents = { new TextContent(text) } });
                    writer.Complete();
                }),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new MainAgentRunResult(
                Text: null,
                TotalInputTokens: 0,
                TotalOutputTokens: 0,
                TurnCount: 1)));

        var hooks = Substitute.For<IHookExecutionService>();
        hooks.FireAsync(Arg.Any<HookPayload>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult(
                hookResult?.Invoke(ci.ArgAt<HookPayload>(0).Point) ?? new AggregatedHookResult()));

        var engine = new QueryStreamEngine(
            NullLogger.Instance,
            runner,
            toolCatalog,
            hooks,
            new ChatSessionDependencies(
                sessionManager,
                new SessionToolSetManager(toolCatalog, toolMetadata),
                new ToolCapabilityResolver(toolCatalog),
                configManager),
            new ChatObservabilityDependencies(
                Substitute.For<ITokenUsageTracker>(),
                new TokenBreakdownEstimator(TestTokenEstimators.Default),
                Substitute.For<INotifierService>()));

        return (engine, runner, hooks);
    }
}
