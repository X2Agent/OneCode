using Microsoft.Extensions.AI;
using NSubstitute;
using OneCode.Core.Hooks;
using OneCode.Infrastructure.Ai;

namespace OneCode.Tests;

/// <summary>
/// ModelCallHookDecorator 单测——pre_model_call / post_model_call 两个拦截点的接线语义：
///   两个节点都触发且按 模型→响应 顺序（纯审计，聚合结果不阻断模型调用）
///   → matcher 值来自 modelId
///   → 流式路径零缓冲：update 逐条透传，post 审计在流结束后触发
///   → 无活跃 hook 时跳过 fire（HasActiveHooks 守卫）
/// </summary>
public sealed class ModelCallHookDecoratorTests
{
    private static (ModelCallHookDecorator Sut, IHookExecutionService Hooks) CreateSut(
        IChatClient inner)
    {
        var hooks = Substitute.For<IHookExecutionService>();
        hooks.HasActiveHooks(
                Arg.Any<HookInterceptionPoint>(), Arg.Any<string?>())
            .Returns(true);
        hooks.FireAsync(
                Arg.Any<HookPayload>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new AggregatedHookResult()));
        return (new ModelCallHookDecorator(inner, hooks), hooks);
    }

    /// <summary>提取 FireAsync 调用的 payload 序列（过滤掉 HasActiveHooks 查询调用）。</summary>
    private static List<HookPayload> FireCalls(IHookExecutionService hooks) =>
        [.. hooks.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(IHookExecutionService.FireAsync))
            .Select(c => (HookPayload)c.GetArguments()[0]!)];

    [Fact]
    public async Task GetResponseAsync_FiresPreThenPostModelCall()
    {
        var inner = new StubChatClient(new ChatResponse(new ChatMessage(ChatRole.Assistant, "answer")));
        var (sut, hooks) = CreateSut(inner);

        var response = await sut.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "hi")],
            new ChatOptions { ModelId = "test-model" },
            TestContext.Current.CancellationToken);

        response.Text.Should().Be("answer");

        var fired = FireCalls(hooks);
        fired.Should().HaveCount(2);
        fired[0].Point.Should().Be(HookInterceptionPoint.PreModelCall);
        fired[1].Point.Should().Be(HookInterceptionPoint.PostModelCall);
    }

    [Fact]
    public async Task GetResponseAsync_PassesModelIdAsMatcherValue()
    {
        var inner = new StubChatClient(new ChatResponse(new ChatMessage(ChatRole.Assistant, "answer")));
        var (sut, hooks) = CreateSut(inner);

        await sut.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "hi")],
            new ChatOptions { ModelId = "gpt-5" },
            TestContext.Current.CancellationToken);

        var matcherValues = FireCalls(hooks)
            .Select(p => p.ModelId)
            .ToList();
        matcherValues.Should().Equal("gpt-5", "gpt-5");
    }

    [Fact]
    public async Task GetResponseAsync_PostModelCall_ProjectsResponseAndFinishReason()
    {
        var inner = new StubChatClient(
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "answer"))
            {
                ModelId = "m-1",
                FinishReason = ChatFinishReason.Stop,
            });
        var (sut, hooks) = CreateSut(inner);

        await sut.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "hi")],
            new ChatOptions { ModelId = "m-1" },
            TestContext.Current.CancellationToken);

        var post = FireCalls(hooks)
            .Single(p => p.Point == HookInterceptionPoint.PostModelCall);

        post.ModelResponse.Should().Be("answer");
        post.FinishReason.Should().Be("stop");
    }

    [Fact]
    public async Task GetResponseAsync_PreModelCall_ProjectsRequestMessages()
    {
        var inner = new StubChatClient(new ChatResponse(new ChatMessage(ChatRole.Assistant, "answer")));
        var (sut, hooks) = CreateSut(inner);

        await sut.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "hi")],
            new ChatOptions { ModelId = "m-1" },
            TestContext.Current.CancellationToken);

        var pre = FireCalls(hooks)
            .Single(p => p.Point == HookInterceptionPoint.PreModelCall);

        pre.RequestMessages.Should().NotBeNull();
        pre.RequestMessages!.Value.ToString().Should().Contain("hi");
    }

    /// <summary>
    /// model 拦截点是纯审计语义（设计决策，非缺口）：deny 聚合结果被丢弃，
    /// 不阻断模型调用——需要阻断的策略应挂 input / pre_tool_call / output。
    /// </summary>
    [Fact]
    public async Task GetResponseAsync_PreModelCallDeny_IsAuditOnly_DoesNotBlockModelCall()
    {
        var inner = new StubChatClient(new ChatResponse(new ChatMessage(ChatRole.Assistant, "answer")));
        var hooks = Substitute.For<IHookExecutionService>();
        hooks.HasActiveHooks(Arg.Any<HookInterceptionPoint>(), Arg.Any<string?>())
            .Returns(true);
        hooks.FireAsync(Arg.Any<HookPayload>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new AggregatedHookResult
            {
                BlockingErrors = [new HookBlockingError("denied", "policy")],
            }));
        var sut = new ModelCallHookDecorator(inner, hooks);

        var response = await sut.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "hi")],
            new ChatOptions { ModelId = "m-1" },
            TestContext.Current.CancellationToken);

        response.Text.Should().Be("answer");
        inner.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task GetResponseAsync_NoActiveHooks_SkipsFireAndPassesThrough()
    {
        var inner = new StubChatClient(new ChatResponse(new ChatMessage(ChatRole.Assistant, "answer")));
        var hooks = Substitute.For<IHookExecutionService>();
        hooks.HasActiveHooks(Arg.Any<HookInterceptionPoint>(), Arg.Any<string?>())
            .Returns(false);
        var sut = new ModelCallHookDecorator(inner, hooks);

        var response = await sut.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "hi")],
            new ChatOptions { ModelId = "m-1" },
            TestContext.Current.CancellationToken);

        response.Text.Should().Be("answer");
        await hooks.DidNotReceive().FireAsync(
            Arg.Any<HookPayload>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetStreamingResponseAsync_StreamsThroughZeroBuffer_ThenFiresPostAudit()
    {
        var inner = new StubChatClient(
        [
            new ChatResponseUpdate(ChatRole.Assistant, "a"),
            new ChatResponseUpdate(ChatRole.Assistant, "b"),
        ]);
        var (sut, hooks) = CreateSut(inner);

        var updates = new List<ChatResponseUpdate>();
        await foreach (var u in sut.GetStreamingResponseAsync(
            [new ChatMessage(ChatRole.User, "hi")],
            new ChatOptions { ModelId = "m-1" },
            TestContext.Current.CancellationToken))
        {
            updates.Add(u);
        }

        updates.Should().HaveCount(2);
        var fired = FireCalls(hooks);
        fired.Should().HaveCount(2);
        fired[0].Point.Should().Be(HookInterceptionPoint.PreModelCall);
        fired[1].Point.Should().Be(HookInterceptionPoint.PostModelCall);
        fired[1].ModelResponse.Should().Be("ab");
    }

    /// <summary>
    /// 零缓冲反证：inner 在产出第二个 update 前等待消费方确认收到第一个。
    /// 若装饰器缓冲整流（回归），第一个 update 只会在流完成后交付，
    /// "先收到第一个"与"后产出第二个"的时间序被破坏，本用例失败。
    /// </summary>
    [Fact]
    public async Task GetStreamingResponseAsync_ZeroBuffer_FirstUpdateDeliveredBeforeStreamCompletes()
    {
        var firstReceived = new TaskCompletionSource<DateTimeOffset>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var inner = new GatedChatClient(firstReceived);
        var (sut, _) = CreateSut(inner);

        var updates = new List<ChatResponseUpdate>();
        await foreach (var u in sut.GetStreamingResponseAsync(
            [new ChatMessage(ChatRole.User, "hi")],
            new ChatOptions { ModelId = "m-1" },
            TestContext.Current.CancellationToken))
        {
            updates.Add(u);
            if (updates.Count == 1)
                firstReceived.TrySetResult(DateTimeOffset.UtcNow);
        }

        updates.Should().HaveCount(2);
        var firstReceivedAt = await firstReceived.Task;
        firstReceivedAt.Should().BeOnOrBefore(inner.SecondUpdateProducedAt);
    }

    private sealed class StubChatClient : IChatClient
    {
        private readonly ChatResponse? _response;
        private readonly IEnumerable<ChatResponseUpdate>? _updates;

        public StubChatClient(ChatResponse response) => _response = response;

        public StubChatClient(IEnumerable<ChatResponseUpdate> updates) => _updates = updates;

        public int CallCount { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(_response!);
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            CallCount++;
            foreach (var update in _updates!)
                yield return update;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        void IDisposable.Dispose() { }
    }

    /// <summary>
    /// 产出一个 update 后等待消费方确认，再记录时刻并产出第二个 update——
    /// 用于证明装饰器交付不晚于流完成（零缓冲）。
    /// </summary>
    private sealed class GatedChatClient(TaskCompletionSource<DateTimeOffset> firstReceived) : IChatClient
    {
        public DateTimeOffset SecondUpdateProducedAt { get; private set; }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, "first");

            // 若装饰器缓冲，消费方永远无法收到 "first" → 此处超时抛 TimeoutException 使测试失败而非挂死
            await firstReceived.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
            SecondUpdateProducedAt = DateTimeOffset.UtcNow;
            yield return new ChatResponseUpdate(ChatRole.Assistant, "second");
        }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException("Streaming only.");

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        void IDisposable.Dispose() { }
    }
}
