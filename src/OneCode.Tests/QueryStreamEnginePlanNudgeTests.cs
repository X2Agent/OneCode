using System.Threading.Channels;
using OneCode.App.Query;
using OneCode.App.Services;
using OneCode.App.Services.Agent;
using OneCode.Infrastructure;
using OneCode.App.Services.Observability;
using OneCode.App.Session;
using OneCode.App.Tools;
using OneCode.Core.Config;
using OneCode.Core.Hooks;
using OneCode.Core.Tools;
using OneCode.Tests.TestSupport;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace OneCode.Tests;

/// <summary>
/// Plan 模式收尾契约回归：模型未调用 SubmitPlan（例如向用户提问澄清）时单轮正常收尾等待用户回复，
/// 不在后台追加强制 SubmitPlan 的二次纠偏请求；收尾后 CacheSafeParams 保留请求级能力边界。
/// </summary>
public sealed class QueryStreamEnginePlanNudgeTests
{
    [Fact]
    public async Task StreamQueryAsync_PlanMode_TextOnlyEnding_CompletesSingleRoundWithoutCorrectiveNudge()
    {
        var ct = TestContext.Current.CancellationToken;
        var (sut, captured) = CreateSut((_, writer) =>
        {
            WriteWithUsage(writer, "请问你想让我做哪一步？1. 检索 2. 合并",
                inputTokens: 100, outputTokens: 10);
            return Task.FromResult(new MainAgentRunResult("plain text question", 100, 10, TurnCount: 3));
        });

        var events = await CollectAsync(
            sut.StreamQueryAsync("调研这个仓库并给出计划", "sys", "model", ct: ct, workingMode: WorkingMode.Plan));

        captured.Should().HaveCount(1, "Plan 模式文本提问或未调 SubmitPlan 收尾时不得自动追加强制提交的二次纠偏轮");
        captured[0].WorkingMode.Should().Be(WorkingMode.Plan);
        events.OfType<NoticeEvent>().Should().BeEmpty("不应发出自动纠偏提示");

        var done = events.OfType<DoneEvent>().Should().ContainSingle().Which;
        done.TerminalReason.Should().Be(RunTerminalReason.Completed);
        done.Usage.Should().NotBeNull();
        done.Usage!.InputTokens.Should().Be(100);
        done.Usage!.OutputTokens.Should().Be(10);
        done.TurnsCompleted.Should().Be(1);
    }

    [Fact]
    public async Task StreamQueryAsync_PlanMode_SubmitPlanAlreadyCalled_DoesNotIssueCorrectiveRun()
    {
        var ct = TestContext.Current.CancellationToken;
        var (sut, captured) = CreateSut((_, writer) =>
        {
            WriteSubmitPlanCall(writer);
            WriteWithUsage(writer, "Plan submitted.", inputTokens: 50, outputTokens: 5);
            return Task.FromResult(new MainAgentRunResult("Plan submitted.", 50, 5, TurnCount: 2));
        });

        var events = await CollectAsync(
            sut.StreamQueryAsync("调研这个仓库并给出计划", "sys", "model", ct: ct, workingMode: WorkingMode.Plan));

        captured.Should().HaveCount(1, "a run that submitted the plan needs no corrective attempt");
        events.Should().NotContain(e => e is NoticeEvent);
        events.OfType<DoneEvent>().Should().ContainSingle();
    }

    [Fact]
    public async Task StreamQueryAsync_BuildMode_TextOnlyEnding_DoesNotIssueCorrectiveRun()
    {
        var ct = TestContext.Current.CancellationToken;
        var (sut, captured) = CreateSut((_, writer) =>
        {
            WriteWithUsage(writer, "Build finished.", inputTokens: 10, outputTokens: 1);
            return Task.FromResult(new MainAgentRunResult("Build finished.", 10, 1, TurnCount: 1));
        });

        var events = await CollectAsync(
            sut.StreamQueryAsync("执行构建", "sys", "model", ct: ct, workingMode: WorkingMode.Build));

        captured.Should().HaveCount(1, "SubmitPlan is a Plan-mode contract; Build runs are never nudged");
        events.Should().NotContain(e => e is NoticeEvent);
        events.OfType<DoneEvent>().Should().ContainSingle();
    }

    [Fact]
    public async Task StreamQueryAsync_PlanMode_RunFailed_DoesNotIssueCorrectiveRun()
    {
        var ct = TestContext.Current.CancellationToken;
        var (sut, captured) = CreateSut((_, writer) =>
            Task.FromException<MainAgentRunResult>(new InvalidOperationException("provider outage")));

        var events = await CollectAsync(
            sut.StreamQueryAsync("调研这个仓库并给出计划", "sys", "model", ct: ct, workingMode: WorkingMode.Plan));

        captured.Should().HaveCount(1, "failed runs end with an error, not a corrective attempt");
        events.Should().Contain(e => e is ErrorEvent);
        events.OfType<DoneEvent>().Should().BeEmpty("a failed run has no normal ending to merge");
    }

    [Fact]
    public async Task StreamQueryAsync_PlanMode_FinalizedCacheSafeParams_CarryRequestCapabilities()
    {
        // 回归守卫：FinalizeAsync 在 StreamCoreAsync 的 finally 复位 ambient 之后重建
        // CacheSafeParams——capabilities 必须显式取请求参数。若读已复位的 ambient，
        // finalize 之后发起的 fork（如 GOAL 子任务 DAG 读 ICacheSafeParamsProvider.Current）
        // 会拿到 null capabilities，父级工具面交集被静默跳过、子代理丢失 Plan 工具。
        var ct = TestContext.Current.CancellationToken;
        var (sut, captured) = CreateSut((_, writer) =>
        {
            WriteWithUsage(writer, "Research finished. Here is a plain-text summary…",
                inputTokens: 100, outputTokens: 10);
            return Task.FromResult(new MainAgentRunResult("plain text", 100, 10, TurnCount: 1));
        });

        await CollectAsync(
            sut.StreamQueryAsync("调研这个仓库并给出计划", "sys", "model", ct: ct, workingMode: WorkingMode.Plan));

        captured.Should().HaveCount(1);
        sut.Current.Should().NotBeNull("查询收尾后 CacheSafeParams 必须已写入，供 finalize 之后的 fork 读取");
        sut.Current!.ToolCapabilities.Should().NotBeNull(
            "finalize 发生在 ambient 复位之后，capabilities 只能来自请求参数而非 ambient");
        sut.Current.ToolCapabilities.Should().BeSameAs(captured[0].ToolCapabilities,
            "finalized 快照必须保留本轮请求解析出的 Plan 能力集");
    }

    private static void WriteWithUsage(ChannelWriter<object> writer, string text, long inputTokens, long outputTokens)
    {
        writer.TryWrite(new AgentResponseUpdate
        {
            Contents = { new UsageContent(new UsageDetails { InputTokenCount = inputTokens, OutputTokenCount = outputTokens }) }
        });
        writer.TryWrite(new AgentResponseUpdate { Contents = { new TextContent(text) } });
    }

    private static void WriteSubmitPlanCall(ChannelWriter<object> writer)
    {
        writer.TryWrite(new AgentResponseUpdate
        {
            Contents =
            {
                new FunctionCallContent(
                    "call_sp_1",
                    "SubmitPlan",
                    new Dictionary<string, object?> { ["content"] = "# plan", ["steps"] = "[]" }),
                new FunctionResultContent("call_sp_1", "submitted"),
            }
        });
    }

    private static async Task<List<QueryEvent>> CollectAsync(IAsyncEnumerable<QueryEvent> stream)
    {
        var events = new List<QueryEvent>();
        await foreach (var evt in stream.WithCancellation(TestContext.Current.CancellationToken))
            events.Add(evt);
        return events;
    }

    /// <summary>
    /// 与 QueryStreamEngineTests.CreateSutWithMockedRunner 同型，但按调用序号编排每轮 runner 行为，
    /// 并捕获每次调用的 <see cref="MainAgentRunOptions"/> 以断言纠偏轮的 prompt / AgentRunId。
    /// </summary>
    private static (QueryStreamEngine Sut, List<MainAgentRunOptions> CapturedOptions) CreateSut(
        Func<int, ChannelWriter<object>, Task<MainAgentRunResult>> runScript)
    {
        var logger = Substitute.For<ILogger<QueryStreamEngine>>();
        var toolMetadata = new ToolMetadataRegistry();
        var toolCatalog = new ToolCatalog(new Lazy<List<AIFunction>>(() => []), toolMetadata, mcpConnectionManager: null);
        var configManager = TestConfigManager.Create();
        configManager.Current.Returns(ConfigSnapshot.FromEffective(new AppSettings()));
        var sessionManager = Substitute.For<ISessionManager>();
        sessionManager.WorkingDirectory.Returns(Path.GetTempPath());

        var runCount = 0;
        var captured = new List<MainAgentRunOptions>();
        var runner = Substitute.For<IMainAgentRunner>();
        runner.RunStreamingAsync(
                Arg.Do<MainAgentRunOptions>(captured.Add),
                Arg.Any<ChannelWriter<object>>(),
                Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                runCount++;
                var writer = callInfo.ArgAt<ChannelWriter<object>>(1);
                var task = runScript(runCount, writer);
                writer.Complete();
                return task;
            });

        var sut = new QueryStreamEngine(logger,
            mainAgentRunner: runner,
            toolCatalog: toolCatalog,
            hookExecutionService: Substitute.For<IHookExecutionService>(),
            session: new ChatSessionDependencies(
                sessionManager,
                new SessionToolSetManager(toolCatalog, toolMetadata),
                new ToolCapabilityResolver(toolCatalog),
                configManager),
            observability: new ChatObservabilityDependencies(
                Substitute.For<ITokenUsageTracker>(),
                new TokenBreakdownEstimator(TestSupport.TestTokenEstimators.Default),
                Substitute.For<INotifierService>()));

        return (sut, captured);
    }
}
