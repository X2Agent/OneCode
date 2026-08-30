using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OneCode.Core.Tokens;
using OneCode.Infrastructure.Agent.RunMiddleware;
using OneCode.Infrastructure.Api;


namespace OneCode.Tests;

/// <summary>
/// Unit tests for <see cref="BudgetGuardRunMiddleware"/>.
/// 验证 Agent Run 级 pre-execution 预算守卫：当 TokenLedger 累计 token（输入+输出）
/// 达 MaxBudgetTokens 时短路返回错误响应，不调用内层 agent；未超支时正常放行。
/// 覆盖非流式/流式路径、null 参数 pass-through、边界条件（恰好等于）等场景。
/// </summary>
public sealed class BudgetGuardRunMiddlewareTests
{
    private static TokenLedger CreateTracker(long initialTokens = 0)
    {
        var tracker = new TokenLedger();

        if (initialTokens > 0)
            tracker.RecordUsage(new UsageRecord("claude-sonnet-4", (int)Math.Min(initialTokens, int.MaxValue), 0));

        return tracker;
    }

    private static AgentResponse CreateResponse(string text = "OK")
    {
        var response = new AgentResponse
        {
            Messages = [new ChatMessage(ChatRole.Assistant, text)],
        };
        return response;
    }

    private static AgentResponseUpdate CreateUpdate(string? text = null)
    {
        var update = new AgentResponseUpdate();
        if (text is not null)
            update.Contents.Add(new TextContent(text));
        return update;
    }

    // Non-streaming (RunAsync)

    [Fact]
    public async Task RunAsync_BudgetNotExceeded_DelegatesToAgent()
    {
        var tracker = CreateTracker(initialTokens: 1_000_000);
        var stubAgent = new StubAgent(CreateResponse("result"));
        var (runFunc, _) = BudgetGuardRunMiddleware.Create(tracker, maxBudgetTokens: 5_000_000, null);

        var response = await runFunc([], null, null, stubAgent, TestContext.Current.CancellationToken);

        response.Text.Should().Be("result");
    }

    [Fact]
    public async Task RunAsync_BudgetExceeded_ShortCircuitsWithoutCallingAgent()
    {
        var tracker = CreateTracker(initialTokens: 10_000_000);
        // StubAgent would throw if called (no response set for this constructor path),
        // but we set a response anyway to detect if it was incorrectly called.
        var stubAgent = new StubAgent(CreateResponse("should-not-reach"));
        var (runFunc, _) = BudgetGuardRunMiddleware.Create(tracker, maxBudgetTokens: 5_000_000, null);

        var response = await runFunc([], null, null, stubAgent, TestContext.Current.CancellationToken);

        // Should NOT contain the agent's response — must be the budget-exceeded message
        response.Text.Should().NotBe("should-not-reach");
        response.Text.Should().Contain("Budget Exceeded");
        response.Text.Should().Contain("10,000,000");
        response.Text.Should().Contain("5,000,000");
        // No LLM call was made, so Usage must be null
        response.Usage.Should().BeNull();
    }

    [Fact]
    public async Task RunAsync_BudgetExactlyAtLimit_ShortCircuits()
    {
        // >= check: tokens == limit should trigger short-circuit (prevent runaway at the boundary)
        var tracker = CreateTracker(initialTokens: 5_000_000);
        var stubAgent = new StubAgent(CreateResponse("should-not-reach"));
        var (runFunc, _) = BudgetGuardRunMiddleware.Create(tracker, maxBudgetTokens: 5_000_000, null);

        var response = await runFunc([], null, null, stubAgent, TestContext.Current.CancellationToken);

        response.Text.Should().Contain("Budget Exceeded");
        response.Usage.Should().BeNull();
    }

    [Fact]
    public async Task RunAsync_BudgetJustUnderLimit_DelegatesToAgent()
    {
        var tracker = CreateTracker(initialTokens: 4_990_000);
        var stubAgent = new StubAgent(CreateResponse("result"));
        var (runFunc, _) = BudgetGuardRunMiddleware.Create(tracker, maxBudgetTokens: 5_000_000, null);

        var response = await runFunc([], null, null, stubAgent, TestContext.Current.CancellationToken);

        response.Text.Should().Be("result");
    }

    [Fact]
    public async Task RunAsync_NullTokenLedger_PassThroughWithoutCheck()
    {
        // No TokenLedger → no budget enforcement, even if maxBudgetTokens is set
        var stubAgent = new StubAgent(CreateResponse("result"));
        var (runFunc, _) = BudgetGuardRunMiddleware.Create(null, maxBudgetTokens: 1, null);

        var response = await runFunc([], null, null, stubAgent, TestContext.Current.CancellationToken);

        response.Text.Should().Be("result");
    }

    [Fact]
    public async Task RunAsync_NullMaxBudgetTokens_PassThroughWithoutCheck()
    {
        // No budget limit → no enforcement, even if TokenLedger has high usage
        var tracker = CreateTracker(initialTokens: 1_000_000_000);
        var stubAgent = new StubAgent(CreateResponse("result"));
        var (runFunc, _) = BudgetGuardRunMiddleware.Create(tracker, maxBudgetTokens: null, null);

        var response = await runFunc([], null, null, stubAgent, TestContext.Current.CancellationToken);

        response.Text.Should().Be("result");
    }

    // Streaming (RunStreamingAsync)

    [Fact]
    public async Task RunStreamingAsync_BudgetNotExceeded_DelegatesToAgent()
    {
        var tracker = CreateTracker(initialTokens: 1_000_000);
        var updates = new[]
        {
            CreateUpdate("Hello"),
            CreateUpdate(" world"),
        }.ToAsyncEnumerable();
        var stubAgent = new StubAgent(updates);
        var (_, runStreamingFunc) = BudgetGuardRunMiddleware.Create(tracker, maxBudgetTokens: 5_000_000, null);

        var results = new List<AgentResponseUpdate>();
        await foreach (var update in runStreamingFunc([], null, null, stubAgent, TestContext.Current.CancellationToken))
            results.Add(update);

        results.Should().HaveCount(2);
        results[0].Text.Should().Be("Hello");
        results[1].Text.Should().Be(" world");
    }

    [Fact]
    public async Task RunStreamingAsync_BudgetExceeded_ShortCircuitsWithSingleUpdate()
    {
        var tracker = CreateTracker(initialTokens: 10_000_000);
        var updates = new[]
        {
            CreateUpdate("should-not-reach"),
        }.ToAsyncEnumerable();
        var stubAgent = new StubAgent(updates);
        var (_, runStreamingFunc) = BudgetGuardRunMiddleware.Create(tracker, maxBudgetTokens: 5_000_000, null);

        var results = new List<AgentResponseUpdate>();
        await foreach (var update in runStreamingFunc([], null, null, stubAgent, TestContext.Current.CancellationToken))
            results.Add(update);

        results.Should().HaveCount(1);
        results[0].Text.Should().Contain("Budget Exceeded");
        results[0].Text.Should().Contain("10,000,000");
    }

    [Fact]
    public async Task RunStreamingAsync_NullTokenLedger_PassThrough()
    {
        var updates = new[]
        {
            CreateUpdate("result"),
        }.ToAsyncEnumerable();
        var stubAgent = new StubAgent(updates);
        var (_, runStreamingFunc) = BudgetGuardRunMiddleware.Create(null, maxBudgetTokens: 1, null);

        var results = new List<AgentResponseUpdate>();
        await foreach (var update in runStreamingFunc([], null, null, stubAgent, TestContext.Current.CancellationToken))
            results.Add(update);

        results.Should().HaveCount(1);
        results[0].Text.Should().Be("result");
    }

    // Message formatting

    [Fact]
    public void FormatBudgetExceededMessage_IncludesBothAmounts()
    {
        var message = BudgetGuardRunMiddleware.FormatBudgetExceededMessage(12_345_678, 10_000_000);

        message.Should().Contain("12,345,678");
        message.Should().Contain("10,000,000");
        message.Should().Contain("Budget Exceeded");
    }

    [Fact]
    public void CreateBudgetExceededResponse_HasMessageButNoUsage()
    {
        var response = BudgetGuardRunMiddleware.CreateBudgetExceededResponse("test message");

        response.Text.Should().Be("test message");
        response.Usage.Should().BeNull();
    }

    [Fact]
    public void CreateBudgetExceededUpdate_HasTextContent()
    {
        var update = BudgetGuardRunMiddleware.CreateBudgetExceededUpdate("test message");

        update.Text.Should().Be("test message");
    }

    // Integration: BudgetGuard + UsageTracking collaboration

    [Fact]
    public async Task MultipleRuns_BudgetGuardBlocksAfterUsageTrackingRecords()
    {
        // Simulate the real middleware chain: BudgetGuard (outer) → UsageTracking (inner) → agent.
        // Run 1: tokens under budget → agent runs, usage recorded.
        // Run 2: tokens now at/over budget → BudgetGuard short-circuits, agent not called.
        var tracker = CreateTracker();
        var usage = new UsageDetails
        {
            InputTokenCount = 2_000_000,
            OutputTokenCount = 0,
        };
        var responseWithUsage = new AgentResponse
        {
            Usage = usage,
            Messages = [new ChatMessage(ChatRole.Assistant, "run-1-result")],
        };

        var maxBudget = 1_000_000L; // After run 1 (2M tokens), budget exceeded

        // Run 1: tokens = 0 < 1M → should execute
        var stubAgent1 = new StubAgent(responseWithUsage);
        var (guardRun, _) = BudgetGuardRunMiddleware.Create(tracker, maxBudget, null);
        var (usageRun, _) = UsageTrackingRunMiddleware.Create(tracker, "claude-sonnet-4", null);

        // Step 1: Run through UsageTracking directly (simulates BudgetGuard passing through)
        var response1 = await usageRun([], null, null, stubAgent1, TestContext.Current.CancellationToken);
        response1.Text.Should().Be("run-1-result");
        tracker.GetTotalTokens().Should().Be(2_000_000);

        // Step 2: Now BudgetGuard should block (2,000,000 >= 1,000,000)
        var stubAgent2 = new StubAgent(new AgentResponse
        {
            Messages = [new ChatMessage(ChatRole.Assistant, "should-not-reach")],
        });
        var response2 = await guardRun([], null, null, stubAgent2, TestContext.Current.CancellationToken);

        response2.Text.Should().Contain("Budget Exceeded");
        response2.Text.Should().Contain("2,000,000");
        response2.Usage.Should().BeNull();
    }
}
