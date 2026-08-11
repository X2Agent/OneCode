using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OneCode.Core.Cost;
using OneCode.Infrastructure.Agent.RunMiddleware;
using OneCode.Infrastructure.Api;


namespace OneCode.Tests;

/// <summary>
/// Unit tests for <see cref="BudgetGuardRunMiddleware"/>.
/// 验证 Agent Run 级 pre-execution 预算守卫：当 CostTracker 累计成本达 MaxBudgetUsd 时
/// 短路返回错误响应，不调用内层 agent；未超支时正常放行。
/// 覆盖非流式/流式路径、null 参数 pass-through、边界条件（恰好等于）等场景。
/// </summary>
public sealed class BudgetGuardRunMiddlewareTests
{
    // Use $1/M pricing so initialCost dollars = initialCost * 1_000_000 tokens (exact for decimal).
    // This avoids floating-point truncation issues at boundary conditions (cost == limit).
    private static readonly ModelPricing TestPricing = new(1m, 1m, 0m, 0m);

    private static CostTracker CreateTracker(decimal initialCost = 0m)
    {
        var tracker = new CostTracker(configuredPricing: new Dictionary<string, ModelPricingTiered>
        {
            ["claude-sonnet-4"] = new ModelPricingTiered(TestPricing),
        });

        // Seed initial cost: at $1/M, initialCost dollars = initialCost * 1_000_000 tokens.
        if (initialCost > 0m)
        {
            var inputTokens = (int)(initialCost * 1_000_000m);
            tracker.RecordUsage(new UsageRecord("claude-sonnet-4", inputTokens, 0));
        }

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
        var tracker = CreateTracker(initialCost: 1.0m);
        var stubAgent = new StubAgent(CreateResponse("result"));
        var (runFunc, _) = BudgetGuardRunMiddleware.Create(tracker, maxBudgetUsd: 5.0m, null);

        var response = await runFunc([], null, null, stubAgent, CancellationToken.None);

        response.Text.Should().Be("result");
    }

    [Fact]
    public async Task RunAsync_BudgetExceeded_ShortCircuitsWithoutCallingAgent()
    {
        var tracker = CreateTracker(initialCost: 10.0m);
        // StubAgent would throw if called (no response set for this constructor path),
        // but we set a response anyway to detect if it was incorrectly called.
        var stubAgent = new StubAgent(CreateResponse("should-not-reach"));
        var (runFunc, _) = BudgetGuardRunMiddleware.Create(tracker, maxBudgetUsd: 5.0m, null);

        var response = await runFunc([], null, null, stubAgent, CancellationToken.None);

        // Should NOT contain the agent's response — must be the budget-exceeded message
        response.Text.Should().NotBe("should-not-reach");
        response.Text.Should().Contain("Budget Exceeded");
        response.Text.Should().Contain("$10.0000");
        response.Text.Should().Contain("$5.0000");
        // No LLM call was made, so Usage must be null
        response.Usage.Should().BeNull();
    }

    [Fact]
    public async Task RunAsync_BudgetExactlyAtLimit_ShortCircuits()
    {
        // >= check: cost == limit should trigger short-circuit (prevent overspending at the boundary)
        var tracker = CreateTracker(initialCost: 5.0m);
        var stubAgent = new StubAgent(CreateResponse("should-not-reach"));
        var (runFunc, _) = BudgetGuardRunMiddleware.Create(tracker, maxBudgetUsd: 5.0m, null);

        var response = await runFunc([], null, null, stubAgent, CancellationToken.None);

        response.Text.Should().Contain("Budget Exceeded");
        response.Usage.Should().BeNull();
    }

    [Fact]
    public async Task RunAsync_BudgetJustUnderLimit_DelegatesToAgent()
    {
        var tracker = CreateTracker(initialCost: 4.99m);
        var stubAgent = new StubAgent(CreateResponse("result"));
        var (runFunc, _) = BudgetGuardRunMiddleware.Create(tracker, maxBudgetUsd: 5.0m, null);

        var response = await runFunc([], null, null, stubAgent, CancellationToken.None);

        response.Text.Should().Be("result");
    }

    [Fact]
    public async Task RunAsync_NullCostTracker_PassThroughWithoutCheck()
    {
        // No CostTracker → no budget enforcement, even if maxBudgetUsd is set
        var stubAgent = new StubAgent(CreateResponse("result"));
        var (runFunc, _) = BudgetGuardRunMiddleware.Create(null, maxBudgetUsd: 0.01m, null);

        var response = await runFunc([], null, null, stubAgent, CancellationToken.None);

        response.Text.Should().Be("result");
    }

    [Fact]
    public async Task RunAsync_NullMaxBudgetUsd_PassThroughWithoutCheck()
    {
        // No budget limit → no enforcement, even if CostTracker has high cost
        var tracker = CreateTracker(initialCost: 1000.0m);
        var stubAgent = new StubAgent(CreateResponse("result"));
        var (runFunc, _) = BudgetGuardRunMiddleware.Create(tracker, maxBudgetUsd: null, null);

        var response = await runFunc([], null, null, stubAgent, CancellationToken.None);

        response.Text.Should().Be("result");
    }

    // Streaming (RunStreamingAsync)

    [Fact]
    public async Task RunStreamingAsync_BudgetNotExceeded_DelegatesToAgent()
    {
        var tracker = CreateTracker(initialCost: 1.0m);
        var updates = new[]
        {
            CreateUpdate("Hello"),
            CreateUpdate(" world"),
        }.ToAsyncEnumerable();
        var stubAgent = new StubAgent(updates);
        var (_, runStreamingFunc) = BudgetGuardRunMiddleware.Create(tracker, maxBudgetUsd: 5.0m, null);

        var results = new List<AgentResponseUpdate>();
        await foreach (var update in runStreamingFunc([], null, null, stubAgent, CancellationToken.None))
            results.Add(update);

        results.Should().HaveCount(2);
        results[0].Text.Should().Be("Hello");
        results[1].Text.Should().Be(" world");
    }

    [Fact]
    public async Task RunStreamingAsync_BudgetExceeded_ShortCircuitsWithSingleUpdate()
    {
        var tracker = CreateTracker(initialCost: 10.0m);
        var updates = new[]
        {
            CreateUpdate("should-not-reach"),
        }.ToAsyncEnumerable();
        var stubAgent = new StubAgent(updates);
        var (_, runStreamingFunc) = BudgetGuardRunMiddleware.Create(tracker, maxBudgetUsd: 5.0m, null);

        var results = new List<AgentResponseUpdate>();
        await foreach (var update in runStreamingFunc([], null, null, stubAgent, CancellationToken.None))
            results.Add(update);

        results.Should().HaveCount(1);
        results[0].Text.Should().Contain("Budget Exceeded");
        results[0].Text.Should().Contain("$10.0000");
    }

    [Fact]
    public async Task RunStreamingAsync_NullCostTracker_PassThrough()
    {
        var updates = new[]
        {
            CreateUpdate("result"),
        }.ToAsyncEnumerable();
        var stubAgent = new StubAgent(updates);
        var (_, runStreamingFunc) = BudgetGuardRunMiddleware.Create(null, maxBudgetUsd: 0.01m, null);

        var results = new List<AgentResponseUpdate>();
        await foreach (var update in runStreamingFunc([], null, null, stubAgent, CancellationToken.None))
            results.Add(update);

        results.Should().HaveCount(1);
        results[0].Text.Should().Be("result");
    }

    // Message formatting

    [Fact]
    public void FormatBudgetExceededMessage_IncludesBothAmounts()
    {
        var message = BudgetGuardRunMiddleware.FormatBudgetExceededMessage(12.3456m, 10.0m);

        message.Should().Contain("$12.3456");
        message.Should().Contain("$10.0000");
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
        // Run 1: cost under budget → agent runs, usage recorded.
        // Run 2: cost now at/over budget → BudgetGuard short-circuits, agent not called.
        var tracker = CreateTracker(initialCost: 0m);
        var usage = new UsageDetails
        {
            InputTokenCount = 2_000_000, // 2M * $1/M = $2.00 (TestPricing)
            OutputTokenCount = 0,
        };
        var responseWithUsage = new AgentResponse
        {
            Usage = usage,
            Messages = [new ChatMessage(ChatRole.Assistant, "run-1-result")],
        };

        var maxBudget = 1.0m; // After run 1 ($2.00), budget exceeded

        // Run 1: cost = 0 < 1.0 → should execute
        var stubAgent1 = new StubAgent(responseWithUsage);
        var (guardRun, _) = BudgetGuardRunMiddleware.Create(tracker, maxBudget, null);
        var (usageRun, _) = UsageTrackingRunMiddleware.Create(tracker, "claude-sonnet-4", null);

        // Step 1: Run through UsageTracking directly (simulates BudgetGuard passing through)
        var response1 = await usageRun([], null, null, stubAgent1, CancellationToken.None);
        response1.Text.Should().Be("run-1-result");
        tracker.GetTotalCost().Should().Be(2.00m);

        // Step 2: Now BudgetGuard should block (cost $2.00 >= limit $1.00)
        var stubAgent2 = new StubAgent(new AgentResponse
        {
            Messages = [new ChatMessage(ChatRole.Assistant, "should-not-reach")],
        });
        var response2 = await guardRun([], null, null, stubAgent2, CancellationToken.None);

        response2.Text.Should().Contain("Budget Exceeded");
        response2.Text.Should().Contain("$2.0000");
        response2.Usage.Should().BeNull();
    }
}
