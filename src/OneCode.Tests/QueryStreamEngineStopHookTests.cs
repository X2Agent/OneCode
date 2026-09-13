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
/// 覆盖 <see cref="QueryStreamEngine"/> 的 Stop-hook 纠偏续跑循环——
/// 主循环中唯一此前无任何测试的分支（Hook 基础设施另有 HookExecutionServiceTests 覆盖）。
///
/// 契约：Stop hook 阻断时以阻断反馈重入同一会话继续跑，连续阻断超过
/// <c>MaxStopCorrections</c>（3）后降级为警告并照常终结，避免死循环。
/// </summary>
public sealed class QueryStreamEngineStopHookTests
{
    private const int MaxStopCorrections = 3;

    [Fact]
    public async Task StreamInteractiveAsync_StopHookAllowsCompletion_DoesNotRetry()
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
    /// 连续阻断达到上限即终结：纠偏 3 次后第 4 次阻断降级为警告。
    /// 若上限逻辑失效（例如误写成死循环），本用例会因运行次数超限而失败。
    /// </summary>
    [Fact]
    public async Task StreamInteractiveAsync_StopHookAlwaysBlocks_RetriesUpToLimitThenFinalizes()
    {
        var ct = TestContext.Current.CancellationToken;
        var (engine, runner, _) = CreateEngine(
            textForRound: n => $"round-{n}",
            hookResult: evt => evt == HookEvent.Stop
                ? Blocking("needs more work")
                : new AggregatedHookResult());

        var events = await CollectAsync(engine, ct);

        await runner.Received(MaxStopCorrections + 1).RunStreamingAsync(
            Arg.Any<MainAgentRunOptions>(), Arg.Any<ChannelWriter<object>>(), Arg.Any<CancellationToken>());

        events.OfType<ErrorEvent>().Should().Contain(e => e.Message.Contains("Stop hook blocked completion"));
        events.OfType<DoneEvent>().Should().ContainSingle();
    }

    /// <summary>阻断反馈会重入并继续产出文本，跨轮文本应聚合进最终 DoneEvent。</summary>
    [Fact]
    public async Task StreamInteractiveAsync_StopHookBlocksThenAllows_AggregatesTextAcrossRounds()
    {
        var ct = TestContext.Current.CancellationToken;
        var stopBlocks = 0;
        var (engine, runner, _) = CreateEngine(
            textForRound: n => $"round-{n}",
            hookResult: evt =>
            {
                if (evt != HookEvent.Stop)
                    return new AggregatedHookResult();
                stopBlocks++;
                return stopBlocks <= 2
                    ? Blocking($"correction-{stopBlocks}")
                    : new AggregatedHookResult();
            });

        var events = await CollectAsync(engine, ct);

        await runner.Received(3).RunStreamingAsync(
            Arg.Any<MainAgentRunOptions>(), Arg.Any<ChannelWriter<object>>(), Arg.Any<CancellationToken>());
        events.OfType<DoneEvent>().Should().ContainSingle()
            .Which.FullText.Should().Be("round-1round-2round-3");
        // 每次阻断都应发出反馈事件，但不得出现"达到上限"的终结警告。
        events.OfType<ErrorEvent>().Should().OnlyContain(e => e.Message.Contains("Stop hook feedback"));
    }

    [Fact]
    public async Task StreamInteractiveAsync_UserPromptSubmitHookBlocks_SkipsRunEntirely()
    {
        var ct = TestContext.Current.CancellationToken;
        var (engine, runner, _) = CreateEngine(
            textForRound: n => $"round-{n}",
            hookResult: evt => evt == HookEvent.UserPromptSubmit
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
        Func<HookEvent, AggregatedHookResult>? hookResult = null)
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
                hookResult?.Invoke(ci.ArgAt<HookPayload>(0).Event) ?? new AggregatedHookResult()));

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
