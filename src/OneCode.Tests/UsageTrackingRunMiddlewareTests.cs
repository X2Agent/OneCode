using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OneCode.Infrastructure.Agent.RunMiddleware;
using OneCode.Infrastructure.Api;


namespace OneCode.Tests;

/// <summary>
/// Unit tests for <see cref="UsageTrackingRunMiddleware"/>.
/// 验证 Agent Run 级中间件正确提取 LLM Usage 并写入 TokenLedger，
/// 覆盖流式/非流式路径、cache token 提取、null TokenLedger 等场景。
/// </summary>
public sealed class UsageTrackingRunMiddlewareTests
{
    private static TokenLedger CreateTracker() => new();

    private static AgentResponse CreateResponse(UsageDetails? usage)
    {
        var response = new AgentResponse
        {
            Usage = usage,
        };
        return response;
    }

    private static AgentResponseUpdate CreateUpdate(UsageDetails? usage = null, string? text = null)
    {
        var update = new AgentResponseUpdate();
        if (usage is not null)
            update.Contents.Add(new UsageContent(usage));
        if (text is not null)
            update.Contents.Add(new TextContent(text));
        return update;
    }

    private static UsageDetails CreateUsage(
        long? input = null, long? output = null,
        long? cacheRead = null, long? reasoning = null,
        long? cacheWrite = null)
    {
        var details = new UsageDetails
        {
            InputTokenCount = input,
            OutputTokenCount = output,
            CachedInputTokenCount = cacheRead,
            ReasoningTokenCount = reasoning,
        };
        if (cacheWrite is not null)
        {
            details.AdditionalCounts = new AdditionalPropertiesDictionary<long>
            {
                ["cache_creation_input_tokens"] = cacheWrite.Value,
            };
        }
        return details;
    }

    // Non-streaming (RunAsync)

    [Fact]
    public async Task RunAsync_RecordsUsageToTokenLedger()
    {
        var tracker = CreateTracker();
        // InputTokens=1.5M 已含 CacheReadTokens=0.5M 子集（MEAI 契约）
        var usage = CreateUsage(input: 1_500_000, output: 1_000_000, cacheRead: 500_000, cacheWrite: 200_000);
        var stubAgent = new StubAgent(CreateResponse(usage));
        var (runFunc, _) = UsageTrackingRunMiddleware.Create(tracker, "claude-sonnet-4", null);

        await runFunc([], null, null, stubAgent, TestContext.Current.CancellationToken);

        // 输入 + 输出 = 1.5M + 1M（缓存读取计入输入，不重复相加）
        tracker.GetTotalTokens().Should().Be(2_500_000);
    }

    [Fact]
    public async Task RunAsync_ExtractsCacheWriteFromAdditionalCounts()
    {
        var tracker = CreateTracker();
        var usage = CreateUsage(input: 1_000_000, output: 0, cacheWrite: 1_000_000);
        var stubAgent = new StubAgent(CreateResponse(usage));
        var (runFunc, _) = UsageTrackingRunMiddleware.Create(tracker, "claude-sonnet-4", null);

        await runFunc([], null, null, stubAgent, TestContext.Current.CancellationToken);

        // CacheWrite 独立提取，不并入输入/输出：总 token = 1M
        tracker.GetTotalTokens().Should().Be(1_000_000);
    }

    [Fact]
    public async Task RunAsync_ReasoningTokensDoNotDoubleCount()
    {
        var tracker = CreateTracker();
        var usage = CreateUsage(input: 1_000_000, output: 0, reasoning: 500_000);
        var stubAgent = new StubAgent(CreateResponse(usage));
        var (runFunc, _) = UsageTrackingRunMiddleware.Create(tracker, "claude-sonnet-4", null);

        await runFunc([], null, null, stubAgent, TestContext.Current.CancellationToken);

        // ReasoningTokens 是 OutputTokens 的子集（此处 output=0），不单独入账
        tracker.GetTotalTokens().Should().Be(1_000_000);
    }

    [Fact]
    public async Task RunAsync_NullTokenLedger_PassThroughWithoutError()
    {
        var usage = CreateUsage(input: 100, output: 50);
        var stubAgent = new StubAgent(CreateResponse(usage));
        var (runFunc, _) = UsageTrackingRunMiddleware.Create(null, "claude-sonnet-4", null);

        var response = await runFunc([], null, null, stubAgent, TestContext.Current.CancellationToken);

        response.Should().NotBeNull();
        response.Usage.Should().NotBeNull();
        // null TokenLedger 只跳过记账，usage 本身必须原样透传
        response.Usage!.InputTokenCount.Should().Be(100);
        response.Usage.OutputTokenCount.Should().Be(50);
    }

    [Fact]
    public async Task RunAsync_NullUsage_DoesNotRecord()
    {
        var tracker = CreateTracker();
        var stubAgent = new StubAgent(CreateResponse(null));
        var (runFunc, _) = UsageTrackingRunMiddleware.Create(tracker, "claude-sonnet-4", null);

        await runFunc([], null, null, stubAgent, TestContext.Current.CancellationToken);

        tracker.GetTotalTokens().Should().Be(0);
    }

    [Fact]
    public async Task RunAsync_ZeroTokenUsage_DoesNotRecord()
    {
        var tracker = CreateTracker();
        var usage = CreateUsage(input: 0, output: 0);
        var stubAgent = new StubAgent(CreateResponse(usage));
        var (runFunc, _) = UsageTrackingRunMiddleware.Create(tracker, "claude-sonnet-4", null);

        await runFunc([], null, null, stubAgent, TestContext.Current.CancellationToken);

        tracker.GetTotalTokens().Should().Be(0);
    }

    // Streaming (RunStreamingAsync)

    [Fact]
    public async Task RunStreamingAsync_RecordsUsageFromLastUpdate()
    {
        var tracker = CreateTracker();
        // InputTokens=1.2M 已含 CacheReadTokens=0.2M 子集（MEAI 契约）；非缓存部分 = 1M
        var updates = new[]
        {
            CreateUpdate(text: "Hello"),
            CreateUpdate(text: " world"),
            CreateUpdate(usage: CreateUsage(input: 1_200_000, output: 500_000, cacheRead: 200_000, cacheWrite: 100_000)),
        }.ToAsyncEnumerable();
        var stubAgent = new StubAgent(updates);
        var (_, runStreamingFunc) = UsageTrackingRunMiddleware.Create(tracker, "claude-sonnet-4", null);

        var results = new List<AgentResponseUpdate>();
        await foreach (var update in runStreamingFunc([], null, null, stubAgent, TestContext.Current.CancellationToken))
            results.Add(update);

        results.Should().HaveCount(3);
        // 输入 1.2M（含 0.2M 缓存读取）+ 输出 0.5M = 1.7M
        tracker.GetTotalTokens().Should().Be(1_700_000);
    }

    [Fact]
    public async Task RunStreamingAsync_NoUsageContent_DoesNotRecord()
    {
        var tracker = CreateTracker();
        var updates = new[]
        {
            CreateUpdate(text: "Hello"),
            CreateUpdate(text: " world"),
        }.ToAsyncEnumerable();
        var stubAgent = new StubAgent(updates);
        var (_, runStreamingFunc) = UsageTrackingRunMiddleware.Create(tracker, "claude-sonnet-4", null);

        await foreach (var _ in runStreamingFunc([], null, null, stubAgent, TestContext.Current.CancellationToken))
        { }

        tracker.GetTotalTokens().Should().Be(0);
    }

    [Fact]
    public async Task RunStreamingAsync_PreservesAllUpdates()
    {
        var tracker = CreateTracker();
        var updates = new[]
        {
            CreateUpdate(text: "A"),
            CreateUpdate(usage: CreateUsage(input: 100, output: 50)),
            CreateUpdate(text: "B"),
        }.ToAsyncEnumerable();
        var stubAgent = new StubAgent(updates);
        var (_, runStreamingFunc) = UsageTrackingRunMiddleware.Create(tracker, "claude-sonnet-4", null);

        var texts = new List<string?>();
        await foreach (var update in runStreamingFunc([], null, null, stubAgent, TestContext.Current.CancellationToken))
            texts.Add(update.Text);

        texts.Should().Equal("A", "", "B");
    }

    // BuildUsageRecord (internal helper)

    [Fact]
    public void BuildUsageRecord_ExtractsAllTokenDimensions()
    {
        var details = CreateUsage(input: 1000, output: 500, cacheRead: 200, cacheWrite: 100, reasoning: 50);

        var record = UsageTrackingRunMiddleware.BuildUsageRecord(details, "claude-sonnet-4");

        record.ModelId.Should().Be("claude-sonnet-4");
        record.InputTokens.Should().Be(1000);
        record.OutputTokens.Should().Be(500);
        record.CacheReadTokens.Should().Be(200);
        record.CacheWriteTokens.Should().Be(100);
    }

    [Fact]
    public void BuildUsageRecord_NullModelId_UsesUnknown()
    {
        var details = CreateUsage(input: 100, output: 50);

        var record = UsageTrackingRunMiddleware.BuildUsageRecord(details, null);

        record.ModelId.Should().Be("unknown");
    }

    [Fact]
    public void BuildUsageRecord_AlternativeCacheWriteKeys()
    {
        var details = new UsageDetails
        {
            InputTokenCount = 100,
            OutputTokenCount = 50,
            AdditionalCounts = new AdditionalPropertiesDictionary<long>
            {
                ["cache_creation"] = 300,
            },
        };

        var record = UsageTrackingRunMiddleware.BuildUsageRecord(details, "test-model");

        record.CacheWriteTokens.Should().Be(300);
    }

    // Accumulation across multiple runs

    [Fact]
    public async Task MultipleRuns_AccumulateTokensInTracker()
    {
        var tracker = CreateTracker();
        var usage = CreateUsage(input: 1_000_000, output: 0);
        var stubAgent = new StubAgent(CreateResponse(usage));
        var (runFunc, _) = UsageTrackingRunMiddleware.Create(tracker, "claude-sonnet-4", null);

        await runFunc([], null, null, stubAgent, TestContext.Current.CancellationToken);
        await runFunc([], null, null, stubAgent, TestContext.Current.CancellationToken);

        // Two runs: 1M + 1M
        tracker.GetTotalTokens().Should().Be(2_000_000);
    }

    // Exception path
    // 验证流式传输因异常中断时，已收到的 usage 仍被写入 TokenLedger，
    // 确保 BudgetGuard 的 pre-execution 预算熔断不会因累计 token 偏低而失效。

    [Fact]
    public async Task RunStreamingAsync_ExceptionAfterUsage_StillRecordsUsageAndPropagates()
    {
        // 模拟：流先产出 text + usage，然后因网络中断/5xx 抛异常
        var tracker = CreateTracker();
        var stubAgent = new StubAgent(StreamWithUsageThenThrow());
        var (_, runStreamingFunc) = UsageTrackingRunMiddleware.Create(tracker, "claude-sonnet-4", null);

        var texts = new List<string?>();
        var act = async () =>
        {
            await foreach (var update in runStreamingFunc([], null, null, stubAgent, TestContext.Current.CancellationToken))
                texts.Add(update.Text);
        };

        // 异常必须向上传播，不被中间件吞掉
        await act.Should().ThrowAsync<InvalidOperationException>(
            "streaming exceptions must propagate through the middleware without being swallowed");

        // 异常前已收到的 usage 仍被记录：输入 1M + 输出 0.5M
        texts.Should().HaveCount(2, "the two updates before the exception must still be yielded");
        tracker.GetTotalTokens().Should().Be(1_500_000,
            "usage received before the exception must still be recorded so BudgetGuard can enforce --max-budget-tokens");
    }

    [Fact]
    public async Task RunStreamingAsync_ExceptionBeforeAnyUsage_PropagatesWithoutRecording()
    {
        // 模拟：流产出无 usage 的 update 后立即抛异常
        var tracker = CreateTracker();
        var stubAgent = new StubAgent(StreamWithoutUsageThenThrow());
        var (_, runStreamingFunc) = UsageTrackingRunMiddleware.Create(tracker, "claude-sonnet-4", null);

        var act = async () =>
        {
            await foreach (var _ in runStreamingFunc([], null, null, stubAgent, TestContext.Current.CancellationToken))
            { }
        };

        await act.Should().ThrowAsync<InvalidOperationException>(
            "streaming exceptions must propagate even when no usage was received");

        tracker.GetTotalTokens().Should().Be(0,
            "no usage received means nothing to record — usage stays at zero");
    }

    /// <summary>产出一个 text update + 一个 usage update，然后抛异常（模拟流中断）。</summary>
    private static async IAsyncEnumerable<AgentResponseUpdate> StreamWithUsageThenThrow()
    {
        yield return CreateUpdate(text: "partial response");
        yield return CreateUpdate(usage: CreateUsage(input: 1_000_000, output: 500_000));
        throw new InvalidOperationException("stream interrupted by server error");
    }

    /// <summary>产出一个无 usage 的 text update，然后抛异常（模拟流在 usage 到达前失败）。</summary>
    private static async IAsyncEnumerable<AgentResponseUpdate> StreamWithoutUsageThenThrow()
    {
        yield return CreateUpdate(text: "starting...");
        throw new InvalidOperationException("stream failed before usage arrived");
    }
}
