using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OneCode.Core.Goals;
using OneCode.Core.Tokens;
using OneCode.Infrastructure.Agent.RunMiddleware;
using OneCode.Infrastructure.Api;
using System.Text.Json;


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

    // 预算基线（BUILD 模式按 run 独立计量）

    [Fact]
    public async Task RunAsync_UsageAtBaseline_ExcludedFromBudget()
    {
        // BUILD 场景：Plan 阶段已消耗 4M，run 启动时快照为基线 → 本次预算只衡量增量
        var tracker = CreateTracker(initialTokens: 4_000_000);
        var stubAgent = new StubAgent(CreateResponse("result"));
        var (runFunc, _) = BudgetGuardRunMiddleware.Create(tracker, maxBudgetTokens: 1_000_000, null, budgetBaselineTokens: 4_000_000);

        var response = await runFunc([], null, null, stubAgent, TestContext.Current.CancellationToken);

        // 增量 0 < 1M → 放行（无基线时 4M >= 1M 会直接短路）
        response.Text.Should().Be("result");
    }

    [Fact]
    public async Task RunAsync_DeltaBeyondBaseline_ShortCircuitsWithDeltaInMessage()
    {
        var tracker = CreateTracker(initialTokens: 4_000_000);
        var baseline = 4_000_000L;
        var budget = 1_000_000L;

        // 第一次 run 消耗 0.8M（增量 < 1M）→ 放行
        var (runFunc, _) = BudgetGuardRunMiddleware.Create(tracker, budget, null, baseline);
        var usageRun = new StubAgent(new AgentResponse
        {
            Usage = new UsageDetails { InputTokenCount = 800_000, OutputTokenCount = 0 },
            Messages = [new ChatMessage(ChatRole.Assistant, "run-1")],
        });
        var (usageRunFunc, _) = UsageTrackingRunMiddleware.Create(tracker, "claude-sonnet-4", null);
        var response1 = await usageRunFunc([], null, null, usageRun, TestContext.Current.CancellationToken);
        response1.Text.Should().Be("run-1");
        tracker.GetTotalTokens().Should().Be(4_800_000);

        // 第二次 run 再消耗 0.5M → 累计增量 1.3M >= 1M → 短路，消息报告的是超出基线的增量
        var usageRun2 = new StubAgent(new AgentResponse
        {
            Usage = new UsageDetails { InputTokenCount = 500_000, OutputTokenCount = 0 },
            Messages = [new ChatMessage(ChatRole.Assistant, "run-2")],
        });
        await usageRunFunc([], null, null, usageRun2, TestContext.Current.CancellationToken);
        tracker.GetTotalTokens().Should().Be(5_300_000);

        var response2 = await runFunc([], null, null,
            new StubAgent(CreateResponse("should-not-reach")), TestContext.Current.CancellationToken);

        response2.Text.Should().Contain("Budget Exceeded");
        response2.Text.Should().Contain("1,300,000");
        response2.Usage.Should().BeNull();
    }

    [Fact]
    public async Task RunStreamingAsync_UsageAtBaseline_ExcludedFromBudget()
    {
        var tracker = CreateTracker(initialTokens: 4_000_000);
        var updates = new[]
        {
            CreateUpdate("result"),
        }.ToAsyncEnumerable();
        var stubAgent = new StubAgent(updates);
        var (_, runStreamingFunc) = BudgetGuardRunMiddleware.Create(tracker, maxBudgetTokens: 1_000_000, null, budgetBaselineTokens: 4_000_000);

        var collected = new List<AgentResponseUpdate>();
        await foreach (var update in runStreamingFunc([], null, null, stubAgent, TestContext.Current.CancellationToken))
        {
            collected.Add(update);
        }

        collected.Should().Contain(u => u.Text == "result");
    }

    // 预算分级策略：70/90% 预警 + 100% 交互询问

    [Fact]
    public async Task RunAsync_CrossingEarlyWarning_PublishesEarlyWarningOncePerLevel()
    {
        var tracker = CreateTracker(initialTokens: 750_000); // 75% of 1M → Early
        var warnings = new List<(GoalBudgetWarningLevel Level, long Used, long Limit)>();
        var stubAgent = new StubAgent(CreateResponse("result"));
        var (runFunc, _) = BudgetGuardRunMiddleware.Create(
            tracker, maxBudgetTokens: 1_000_000, null,
            onBudgetWarning: (level, used, limit) => warnings.Add((level, used, limit)));

        await runFunc([], null, null, stubAgent, TestContext.Current.CancellationToken);
        await runFunc([], null, null, stubAgent, TestContext.Current.CancellationToken);

        // 第二次 run 同级别不重复发布（去重）
        warnings.Should().ContainSingle().Which.Level.Should().Be(GoalBudgetWarningLevel.Early);
        warnings[0].Used.Should().Be(750_000);
        warnings[0].Limit.Should().Be(1_000_000);
    }

    [Fact]
    public async Task RunAsync_CrossingLateWarning_PublishesLateLevel()
    {
        var tracker = CreateTracker(initialTokens: 950_000); // 95% of 1M → Late
        var warnings = new List<GoalBudgetWarningLevel>();
        var stubAgent = new StubAgent(CreateResponse("result"));
        var (runFunc, _) = BudgetGuardRunMiddleware.Create(
            tracker, maxBudgetTokens: 1_000_000, null,
            onBudgetWarning: (level, _, _) => warnings.Add(level));

        await runFunc([], null, null, stubAgent, TestContext.Current.CancellationToken);

        warnings.Should().ContainSingle().Which.Should().Be(GoalBudgetWarningLevel.Late);
    }

    [Fact]
    public async Task RunAsync_UnderEarlyWarningThreshold_NoWarningPublished()
    {
        var tracker = CreateTracker(initialTokens: 500_000); // 50% < 70%
        var warningCount = 0;
        var stubAgent = new StubAgent(CreateResponse("result"));
        var (runFunc, _) = BudgetGuardRunMiddleware.Create(
            tracker, maxBudgetTokens: 1_000_000, null,
            onBudgetWarning: (_, _, _) => warningCount++);

        await runFunc([], null, null, stubAgent, TestContext.Current.CancellationToken);

        warningCount.Should().Be(0);
    }

    [Fact]
    public async Task RunAsync_BudgetExceeded_UserDenies_ShortCircuitsWithoutCallingAgent()
    {
        var tracker = CreateTracker(initialTokens: 2_000_000);
        var handlerCalls = 0;
        var stubAgent = new StubAgent(CreateResponse("should-not-reach"));
        var (runFunc, _) = BudgetGuardRunMiddleware.Create(
            tracker, maxBudgetTokens: 1_000_000, null,
            onBudgetExceeded: (used, limit, _) =>
            {
                handlerCalls++;
                used.Should().Be(2_000_000);
                limit.Should().Be(1_000_000);
                return Task.FromResult(GoalBudgetDecision.Abort);
            });

        var response = await runFunc([], null, null, stubAgent, TestContext.Current.CancellationToken);

        handlerCalls.Should().Be(1);
        response.Text.Should().Contain("Budget Exceeded");
        response.Usage.Should().BeNull();
    }

    [Fact]
    public async Task RunAsync_BudgetExceeded_UserApprovesDouble_RunExecutesWithDoubledLimit()
    {
        var tracker = CreateTracker(initialTokens: 1_000_000); // delta = 1M >= 1M → 询问
        var stubAgent = new StubAgent(CreateResponse("result"));
        var (runFunc, _) = BudgetGuardRunMiddleware.Create(
            tracker, maxBudgetTokens: 1_000_000, null,
            onBudgetExceeded: (_, _, _) => Task.FromResult(GoalBudgetDecision.ContinueWithExtraBudget));

        var response = await runFunc([], null, null, stubAgent, TestContext.Current.CancellationToken);

        // 上限加倍（2M）后放行本轮，模型响应正常返回
        response.Text.Should().Be("result");
    }

    [Fact]
    public async Task RunAsync_BudgetExceeded_UserRequestsWrapUp_InjectsWrapUpTurnAndStopsLaterRuns()
    {
        var tracker = CreateTracker(initialTokens: 2_000_000);
        var capturingAgent = new MessageCapturingAgent(CreateResponse("wrap-up summary"));
        var (runFunc, _) = BudgetGuardRunMiddleware.Create(
            tracker, maxBudgetTokens: 1_000_000, null,
            onBudgetExceeded: (_, _, _) => Task.FromResult(GoalBudgetDecision.WrapUpAndStop));

        var response = await runFunc([], null, null, capturingAgent, TestContext.Current.CancellationToken);
        response.Text.Should().Be("wrap-up summary");

        // 注入的最后一个 turn 携带收尾总结指令
        capturingAgent.LastMessages.Should().NotBeNull();
        capturingAgent.LastMessages!
            .Any(m => m.Text?.Contains("Budget exhausted") == true)
            .Should().BeTrue("wrap-up turn must carry the wrap-up instruction");

        // 收尾后同一 pipeline 的下一次 run 硬停（不再询问、不再调用 agent）
        var second = await runFunc([], null, null, capturingAgent, TestContext.Current.CancellationToken);
        second.Text.Should().Contain("Budget Exceeded");
    }
}

/// <summary>捕获收到的消息列表的桩 Agent（验证收尾 turn 的注入内容）。</summary>
internal sealed class MessageCapturingAgent(AgentResponse response) : AIAgent
{
    public IReadOnlyList<ChatMessage>? LastMessages { get; private set; }

    protected override Task<AgentResponse> RunCoreAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session,
        AgentRunOptions? options,
        CancellationToken cancellationToken)
    {
        LastMessages = messages.ToList();
        return Task.FromResult(response);
    }

    protected override IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session,
        AgentRunOptions? options,
        CancellationToken cancellationToken)
    {
        LastMessages = messages.ToList();
        var update = new AgentResponseUpdate();
        update.Contents.Add(new TextContent(response.Text));
        return SingleUpdate(update);

        static async IAsyncEnumerable<AgentResponseUpdate> SingleUpdate(AgentResponseUpdate update)
        {
            yield return update;
        }
    }

    protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken)
        => throw new NotImplementedException();

    protected override ValueTask<JsonElement> SerializeSessionCoreAsync(
        AgentSession session,
        JsonSerializerOptions? options,
        CancellationToken cancellationToken)
        => throw new NotImplementedException();

    protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(
        JsonElement serialized,
        JsonSerializerOptions? options,
        CancellationToken cancellationToken)
        => throw new NotImplementedException();
}
