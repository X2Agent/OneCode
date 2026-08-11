using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

using OneCode.Core.Cost;
using OneCode.Infrastructure.Agent.RunMiddleware;
using OneCode.Infrastructure.Api;

namespace OneCode.Tests;

/// <summary>
/// CancellationToken 取消传播测试 — 验证关键中间件在取消令牌触发时的行为：
/// 1. BudgetGuardRunMiddleware 非流式路径：取消正确传播 OperationCanceledException
/// 2. BudgetGuardRunMiddleware 流式路径：流式枚举中途取消正确传播
/// 3. BudgetGuardRunMiddleware 预先取消的 token 不启动流式枚举
///
/// 这些测试覆盖资源泄漏关键场景 — 取消传播失败会导致悬挂的 LLM 调用或未释放的连接。
/// </summary>
public sealed class CancellationTokenPropagationTests
{
    // BudgetGuardRunMiddleware: 非流式路径取消传播

    [Fact]
    public async Task BudgetGuard_RunAsync_CancellationPropagates_OperationCanceledException()
    {
        // Arrange: agent 内部模拟取消触发 — 验证中间件不吞掉 OperationCanceledException
        var tracker = CreateTracker(initialCost: 0m);
        var cts = new CancellationTokenSource();
        cts.Cancel();

        var stubAgent = new CancelPropagatingAgent(cts.Token);
        var (runFunc, _) = BudgetGuardRunMiddleware.Create(tracker, maxBudgetUsd: 100m, null);

        // Act & Assert: 取消令牌已触发，agent 内部应抛 OperationCanceledException
        var act = () => runFunc([], null, null, stubAgent, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>(
            "cancellation must propagate through BudgetGuard without being swallowed");
    }

    [Fact]
    public async Task BudgetGuard_RunAsync_PreCancellation_PassesToAgentWhichThrows()
    {
        // Arrange: 预先取消的 token，验证 BudgetGuard 预算未超支时放行到 agent，
        // agent 内部检查 ct 后抛 OperationCanceledException
        var tracker = CreateTracker(initialCost: 0m);
        var cts = new CancellationTokenSource();
        cts.Cancel();

        var agentCallCount = 0;
        var stubAgent = new CountingAgent(() => agentCallCount++);
        var (runFunc, _) = BudgetGuardRunMiddleware.Create(tracker, maxBudgetUsd: 100m, null);

        // Act: 即使预算未超支，预先取消的 token 也应传播到 agent.RunAsync
        try
        {
            _ = await runFunc([], null, null, stubAgent, cts.Token);
        }
        catch (OperationCanceledException)
        {
            // 预期行为：取消传播
        }

        // Assert: agent 的 RunCoreAsync 被调用了（BudgetGuard 预算未超支放行）
        // 但 agent 内部检查到 ct 已取消时应抛 OperationCanceledException
        agentCallCount.Should().Be(1, "BudgetGuard should pass through to agent when budget is not exceeded");
    }

    // BudgetGuardRunMiddleware: 流式路径取消传播

    [Fact]
    public async Task BudgetGuard_RunStreamingAsync_CancellationDuringStreaming_PropagatesException()
    {
        // Arrange: 流式枚举在中途被取消
        var tracker = CreateTracker(initialCost: 0m);
        var cts = new CancellationTokenSource();

        var streamingAgent = new StreamingCancelAgent(cts);
        var (_, runStreamingFunc) = BudgetGuardRunMiddleware.Create(tracker, maxBudgetUsd: 100m, null);

        // Act: 消费流式更新，第一个 update 后触发取消
        var consumedUpdates = new List<AgentResponseUpdate>();
        var act = async () =>
        {
            await foreach (var update in runStreamingFunc([], null, null, streamingAgent, cts.Token))
            {
                consumedUpdates.Add(update);
                if (consumedUpdates.Count == 1)
                    cts.Cancel(); // 在第一个 update 后触发取消
            }
        };

        // Assert: 应抛出 OperationCanceledException
        await act.Should().ThrowAsync<OperationCanceledException>(
            "streaming cancellation must propagate, not hang indefinitely");
        consumedUpdates.Should().HaveCount(1, "at least one update should be consumed before cancellation");
    }

    [Fact]
    public async Task BudgetGuard_RunStreamingAsync_PreCancelledToken_PropagatesBeforeEnumeration()
    {
        // Arrange: 预先取消的 token
        var tracker = CreateTracker(initialCost: 0m);
        var cts = new CancellationTokenSource();
        cts.Cancel();

        var streamingAgent = new NeverEnumeratedAgent();
        var (_, runStreamingFunc) = BudgetGuardRunMiddleware.Create(tracker, maxBudgetUsd: 100m, null);

        // Act: 预先取消的 token 应在流式枚举开始前传播
        var act = async () =>
        {
            await foreach (var _ in runStreamingFunc([], null, null, streamingAgent, cts.Token))
            {
                // 不应到达这里
            }
        };

        await act.Should().ThrowAsync<OperationCanceledException>(
            "pre-cancelled token must propagate before enumeration starts");
    }

    // Helpers

    private static CostTracker CreateTracker(decimal initialCost = 0m)
    {
        // Use $1/M pricing for exact decimal arithmetic
        var pricing = new ModelPricing(1m, 1m, 0m, 0m);
        var tracker = new CostTracker(configuredPricing: new Dictionary<string, ModelPricingTiered>
        {
            ["test-model"] = new ModelPricingTiered(pricing),
        });

        if (initialCost > 0m)
        {
            var inputTokens = (int)(initialCost * 1_000_000m);
            tracker.RecordUsage(new UsageRecord("test-model", inputTokens, 0));
        }

        return tracker;
    }

    // Stub agents for cancellation testing

    /// <summary>
    /// Agent that throws OperationCanceledException when the token is already cancelled.
    /// </summary>
    private sealed class CancelPropagatingAgent : AIAgent
    {
        private readonly CancellationToken _externalCt;

        public CancelPropagatingAgent(CancellationToken externalCt) => _externalCt = externalCt;

        protected override Task<AgentResponse> RunCoreAsync(
            IEnumerable<ChatMessage> messages,
            AgentSession? session,
            AgentRunOptions? options,
            CancellationToken cancellationToken)
        {
            _externalCt.ThrowIfCancellationRequested();
            cancellationToken.ThrowIfCancellationRequested();
            throw new OperationCanceledException(_externalCt);
        }

        protected override IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
            IEnumerable<ChatMessage> messages,
            AgentSession? session,
            AgentRunOptions? options,
            CancellationToken cancellationToken)
            => throw new OperationCanceledException(_externalCt);

        protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken)
            => throw new NotImplementedException();
        protected override ValueTask<JsonElement> SerializeSessionCoreAsync(AgentSession session, JsonSerializerOptions? options, CancellationToken cancellationToken)
            => throw new NotImplementedException();
        protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(JsonElement json, JsonSerializerOptions? options, CancellationToken cancellationToken)
            => throw new NotImplementedException();
    }

    /// <summary>
    /// Agent that increments a counter each time RunCoreAsync is called.
    /// Throws OperationCanceledException if the token is cancelled.
    /// </summary>
    private sealed class CountingAgent : AIAgent
    {
        private readonly Action _onCalled;

        public CountingAgent(Action onCalled) => _onCalled = onCalled;

        protected override Task<AgentResponse> RunCoreAsync(
            IEnumerable<ChatMessage> messages,
            AgentSession? session,
            AgentRunOptions? options,
            CancellationToken cancellationToken)
        {
            _onCalled();
            cancellationToken.ThrowIfCancellationRequested();
            var response = new AgentResponse
            {
                Messages = [new ChatMessage(ChatRole.Assistant, "ok")],
            };
            return Task.FromResult(response);
        }

        protected override IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
            IEnumerable<ChatMessage> messages,
            AgentSession? session,
            AgentRunOptions? options,
            CancellationToken cancellationToken)
            => throw new NotImplementedException();

        protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken)
            => throw new NotImplementedException();
        protected override ValueTask<JsonElement> SerializeSessionCoreAsync(AgentSession session, JsonSerializerOptions? options, CancellationToken cancellationToken)
            => throw new NotImplementedException();
        protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(JsonElement json, JsonSerializerOptions? options, CancellationToken cancellationToken)
            => throw new NotImplementedException();
    }

    /// <summary>
    /// Streaming agent that yields one update, then triggers cancellation.
    /// </summary>
    private sealed class StreamingCancelAgent : AIAgent
    {
        private readonly CancellationTokenSource _cts;

        public StreamingCancelAgent(CancellationTokenSource cts) => _cts = cts;

        protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
            IEnumerable<ChatMessage> messages,
            AgentSession? session,
            AgentRunOptions? options,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            yield return new AgentResponseUpdate { Contents = { new TextContent("first") } };

            // Trigger cancellation after the first update
            _cts.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(50, cancellationToken);
            yield return new AgentResponseUpdate { Contents = { new TextContent("should-not-reach") } };
        }

        protected override Task<AgentResponse> RunCoreAsync(
            IEnumerable<ChatMessage> messages, AgentSession? session, AgentRunOptions? options, CancellationToken cancellationToken)
            => throw new NotImplementedException();
        protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken)
            => throw new NotImplementedException();
        protected override ValueTask<JsonElement> SerializeSessionCoreAsync(AgentSession session, JsonSerializerOptions? options, CancellationToken cancellationToken)
            => throw new NotImplementedException();
        protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(JsonElement json, JsonSerializerOptions? options, CancellationToken cancellationToken)
            => throw new NotImplementedException();
    }

    /// <summary>
    /// Streaming agent whose stream should never be enumerated.
    /// </summary>
    private sealed class NeverEnumeratedAgent : AIAgent
    {
        protected override IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
            IEnumerable<ChatMessage> messages,
            AgentSession? session,
            AgentRunOptions? options,
            CancellationToken cancellationToken)
            => throw new OperationCanceledException(cancellationToken);

        protected override Task<AgentResponse> RunCoreAsync(
            IEnumerable<ChatMessage> messages, AgentSession? session, AgentRunOptions? options, CancellationToken cancellationToken)
            => throw new NotImplementedException();
        protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken)
            => throw new NotImplementedException();
        protected override ValueTask<JsonElement> SerializeSessionCoreAsync(AgentSession session, JsonSerializerOptions? options, CancellationToken cancellationToken)
            => throw new NotImplementedException();
        protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(JsonElement json, JsonSerializerOptions? options, CancellationToken cancellationToken)
            => throw new NotImplementedException();
    }
}
