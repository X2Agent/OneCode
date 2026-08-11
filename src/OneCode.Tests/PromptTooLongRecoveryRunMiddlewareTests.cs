using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using NSubstitute;
using OneCode.Core.Domain;
using OneCode.Core.Hooks;
using OneCode.Infrastructure.Agent.RunMiddleware;

namespace OneCode.Tests;

/// <summary>
/// Unit tests for <see cref="PromptTooLongRecoveryRunMiddleware"/>.
/// 验证 Agent Run 级 PromptTooLong 恢复中间件：
/// - 首次成功时不触发恢复（pass-through）
/// - PromptTooLongException 时截断消息 + 重试
/// - HttpRequestException(413) 时截断消息 + 重试
/// - 达到 maxAttempts 后异常冒泡
/// - hooks (PreCompact/PostCompact) 在恢复时被调用
/// - 流式路径在无 update yield 时重试
/// </summary>
public sealed class PromptTooLongRecoveryRunMiddlewareTests
{
    // Helpers

    private static AgentResponse CreateResponse(string text = "OK")
    {
        return new AgentResponse
        {
            Messages = [new ChatMessage(ChatRole.Assistant, text)],
        };
    }

    private static AgentResponseUpdate CreateUpdate(string? text = null)
    {
        var update = new AgentResponseUpdate();
        if (text is not null)
            update.Contents.Add(new TextContent(text));
        return update;
    }

    private static List<ChatMessage> CreateMessages(int count)
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, "system-prompt"),
        };
        for (int i = 0; i < count; i++)
            messages.Add(new ChatMessage(ChatRole.User, $"user-message-{i}"));
        return messages;
    }

    /// <summary>
    /// Stub agent that throws on specified call indices, then returns a response.
    /// CallIndex 0 = first call, 1 = second call (retry), etc.
    /// </summary>
    private sealed class ThrowingStubAgent : AIAgent
    {
        private readonly AgentResponse _response;
        private readonly IAsyncEnumerable<AgentResponseUpdate>? _stream;
        private readonly HashSet<int> _throwOnCalls;
        private int _callCount;
        private readonly Exception _exception;

        public int CallCount => _callCount;
        public List<IEnumerable<ChatMessage>> ReceivedMessages { get; } = [];

        public ThrowingStubAgent(AgentResponse response, Exception exception, params int[] throwOnCalls)
        {
            _response = response;
            _exception = exception;
            _throwOnCalls = new HashSet<int>(throwOnCalls);
        }

        public ThrowingStubAgent(IAsyncEnumerable<AgentResponseUpdate> stream, Exception exception, params int[] throwOnCalls)
        {
            _stream = stream;
            _exception = exception;
            _throwOnCalls = new HashSet<int>(throwOnCalls);
        }

        protected override Task<AgentResponse> RunCoreAsync(
            IEnumerable<ChatMessage> messages,
            AgentSession? session,
            AgentRunOptions? options,
            CancellationToken cancellationToken)
        {
            ReceivedMessages.Add(messages);
            var call = _callCount++;
            if (_throwOnCalls.Contains(call))
                return Task.FromException<AgentResponse>(_exception);
            return Task.FromResult(_response);
        }

        protected override IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
            IEnumerable<ChatMessage> messages,
            AgentSession? session,
            AgentRunOptions? options,
            CancellationToken cancellationToken)
        {
            ReceivedMessages.Add(messages);
            var call = _callCount++;
            if (_throwOnCalls.Contains(call))
            {
                return ThrowStream(_exception, cancellationToken);
            }
            return _stream!;
        }

        private static async IAsyncEnumerable<AgentResponseUpdate> ThrowStream(
            Exception ex,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            throw ex;
#pragma warning disable CS0162 // unreachable code
            yield break;
#pragma warning restore CS0162
        }

        protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken)
            => throw new NotImplementedException();

        protected override ValueTask<JsonElement> SerializeSessionCoreAsync(
            AgentSession session, JsonSerializerOptions? options, CancellationToken cancellationToken)
            => throw new NotImplementedException();

        protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(
            JsonElement json, JsonSerializerOptions? options, CancellationToken cancellationToken)
            => throw new NotImplementedException();
    }

    private static IHookExecutionService CreateHookService(List<HookEvent>? capturedEvents = null)
    {
        var hookService = Substitute.For<IHookExecutionService>();
        hookService.FireAsync(Arg.Any<HookPayload>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                capturedEvents?.Add(ci.Arg<HookPayload>().Event);
                return new AggregatedHookResult();
            });
        return hookService;
    }

    // Non-streaming (RunAsync)

    [Fact]
    public async Task RunAsync_NoException_PassThrough()
    {
        var agent = new ThrowingStubAgent(CreateResponse("result"), new PromptTooLongException("test"));
        var (runFunc, _) = PromptTooLongRecoveryRunMiddleware.Create(null, null);

        var response = await runFunc([], null, null, agent, CancellationToken.None);

        response.Text.Should().Be("result");
        agent.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task RunAsync_PromptTooLongException_RetriesAndSucceeds()
    {
        var agent = new ThrowingStubAgent(
            CreateResponse("recovered"),
            new PromptTooLongException("too long"),
            0); // throw on first call only

        var (runFunc, _) = PromptTooLongRecoveryRunMiddleware.Create(null, null);

        var response = await runFunc([], null, null, agent, CancellationToken.None);

        response.Text.Should().Be("recovered");
        agent.CallCount.Should().Be(2); // first failed, second succeeded
    }

    [Fact]
    public async Task RunAsync_HttpRequestException413_RetriesAndSucceeds()
    {
        var agent = new ThrowingStubAgent(
            CreateResponse("recovered"),
            new HttpRequestException("Error: prompt is too long for model"),
            0);

        var (runFunc, _) = PromptTooLongRecoveryRunMiddleware.Create(null, null);

        var response = await runFunc([], null, null, agent, CancellationToken.None);

        response.Text.Should().Be("recovered");
        agent.CallCount.Should().Be(2);
    }

    [Fact]
    public async Task RunAsync_AllAttemptsFail_ExceptionPropagates()
    {
        var agent = new ThrowingStubAgent(
            CreateResponse("should-not-reach"),
            new PromptTooLongException("always too long"),
            0, 1, 2, 3); // throw on all calls including final

        var (runFunc, _) = PromptTooLongRecoveryRunMiddleware.Create(null, null, maxAttempts: 2);

        var act = () => runFunc([], null, null, agent, CancellationToken.None);

        await act.Should().ThrowAsync<PromptTooLongException>();
        agent.CallCount.Should().Be(2); // 2 attempts (maxAttempts=2), last one's exception propagates
    }

    [Fact]
    public async Task RunAsync_NonPromptTooLongException_DoesNotRetry()
    {
        var agent = new ThrowingStubAgent(
            CreateResponse("should-not-reach"),
            new InvalidOperationException("unrelated error"),
            0);

        var (runFunc, _) = PromptTooLongRecoveryRunMiddleware.Create(null, null);

        var act = () => runFunc([], null, null, agent, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        agent.CallCount.Should().Be(1); // no retry for non-PromptTooLong
    }

    [Fact]
    public async Task RunAsync_RecoveryTruncatesMessages()
    {
        var messages = CreateMessages(count: 20); // 1 system + 20 user = 21 total
        var agent = new ThrowingStubAgent(
            CreateResponse("recovered"),
            new PromptTooLongException("too long"),
            0);

        var (runFunc, _) = PromptTooLongRecoveryRunMiddleware.Create(
            null, null, maxAttempts: 3, keepLastMessages: 6);

        await runFunc(messages, null, null, agent, CancellationToken.None);

        // First call: original 21 messages
        // Second call (retry): truncated — 1 system + 6 user = 7 messages
        agent.ReceivedMessages.Should().HaveCount(2);
        agent.ReceivedMessages[0].Should().HaveCount(21);
        agent.ReceivedMessages[1].Should().HaveCount(7);
        // System message preserved
        agent.ReceivedMessages[1].First().Role.Should().Be(ChatRole.System);
    }

    [Fact]
    public async Task RunAsync_HooksFired_PreCompactThenPostCompact()
    {
        var firedEvents = new List<HookEvent>();
        var hookService = CreateHookService(firedEvents);
        var agent = new ThrowingStubAgent(
            CreateResponse("recovered"),
            new PromptTooLongException("too long"),
            0);

        var (runFunc, _) = PromptTooLongRecoveryRunMiddleware.Create(hookService, null);

        await runFunc([], null, null, agent, CancellationToken.None);

        firedEvents.Should().HaveCount(2);
        firedEvents[0].Should().Be(HookEvent.PreCompact);
        firedEvents[1].Should().Be(HookEvent.PostCompact);
    }

    [Fact]
    public async Task RunAsync_NoHooks_WhenNoException()
    {
        var firedEvents = new List<HookEvent>();
        var hookService = CreateHookService(firedEvents);
        var agent = new ThrowingStubAgent(
            CreateResponse("result"),
            new PromptTooLongException("test"),
            1); // won't throw on first call

        var (runFunc, _) = PromptTooLongRecoveryRunMiddleware.Create(hookService, null);

        await runFunc([], null, null, agent, CancellationToken.None);

        firedEvents.Should().BeEmpty();
    }

    // Streaming (RunStreamingAsync)

    [Fact]
    public async Task RunStreamingAsync_NoException_PassThrough()
    {
        var stream = new[] { CreateUpdate("chunk1"), CreateUpdate("chunk2") }.ToAsyncEnumerable();
        var agent = new ThrowingStubAgent(stream, new PromptTooLongException("test"));
        var (_, streamFunc) = PromptTooLongRecoveryRunMiddleware.Create(null, null);

        var results = new List<string>();
        await foreach (var update in streamFunc([], null, null, agent, CancellationToken.None))
        {
            if (update.Text is { } text)
                results.Add(text);
        }

        results.Should().Equal(["chunk1", "chunk2"]);
        agent.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task RunStreamingAsync_PromptTooLongBeforeAnyYield_RetriesAndSucceeds()
    {
        var stream = new[] { CreateUpdate("recovered") }.ToAsyncEnumerable();
        var agent = new ThrowingStubAgent(
            stream,
            new PromptTooLongException("too long"),
            0); // throw on first call, succeed on retry

        var (_, streamFunc) = PromptTooLongRecoveryRunMiddleware.Create(null, null);

        var results = new List<string>();
        await foreach (var update in streamFunc([], null, null, agent, CancellationToken.None))
        {
            if (update.Text is { } text)
                results.Add(text);
        }

        results.Should().Equal(["recovered"]);
        agent.CallCount.Should().Be(2);
    }

    [Fact]
    public async Task RunStreamingAsync_AllAttemptsFail_ExceptionPropagates()
    {
        var stream = new[] { CreateUpdate("should-not-reach") }.ToAsyncEnumerable();
        var agent = new ThrowingStubAgent(
            stream,
            new PromptTooLongException("always too long"),
            0, 1, 2, 3);

        var (_, streamFunc) = PromptTooLongRecoveryRunMiddleware.Create(null, null, maxAttempts: 2);

        var act = async () =>
        {
            await foreach (var _ in streamFunc([], null, null, agent, CancellationToken.None)) { }
        };

        await act.Should().ThrowAsync<PromptTooLongException>();
        agent.CallCount.Should().Be(2); // 2 attempts (maxAttempts=2), last one's exception propagates
    }

    [Fact]
    public async Task RunStreamingAsync_HooksFired_OnRecovery()
    {
        var firedEvents = new List<HookEvent>();
        var hookService = CreateHookService(firedEvents);
        var stream = new[] { CreateUpdate("recovered") }.ToAsyncEnumerable();
        var agent = new ThrowingStubAgent(
            stream,
            new PromptTooLongException("too long"),
            0);

        var (_, streamFunc) = PromptTooLongRecoveryRunMiddleware.Create(hookService, null);

        await foreach (var _ in streamFunc([], null, null, agent, CancellationToken.None)) { }

        firedEvents.Should().HaveCount(2);
        firedEvents[0].Should().Be(HookEvent.PreCompact);
        firedEvents[1].Should().Be(HookEvent.PostCompact);
    }

    // TruncateMessages unit tests

    [Fact]
    public void TruncateMessages_KeepsSystemAndLastN()
    {
        var messages = CreateMessages(count: 10); // 1 system + 10 user

        var result = PromptTooLongRecoveryRunMiddleware.TruncateMessages(messages, keepLast: 4);

        result.Should().HaveCount(5); // 1 system + 4 user
        result[0].Role.Should().Be(ChatRole.System);
        // Last 4 user messages: user-message-6, 7, 8, 9
        result.Skip(1).Select(m => m.Text).Should().Equal(
            ["user-message-6", "user-message-7", "user-message-8", "user-message-9"]);
    }

    [Fact]
    public void TruncateMessages_NoTruncation_WhenBelowThreshold()
    {
        var messages = CreateMessages(count: 3); // 1 system + 3 user = 4 total

        var result = PromptTooLongRecoveryRunMiddleware.TruncateMessages(messages, keepLast: 6);

        result.Should().HaveCount(4); // unchanged
    }

    [Fact]
    public void TruncateMessages_NoSystemMessages_KeepsLastN()
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "msg1"),
            new(ChatRole.User, "msg2"),
            new(ChatRole.User, "msg3"),
            new(ChatRole.User, "msg4"),
            new(ChatRole.User, "msg5"),
        };

        var result = PromptTooLongRecoveryRunMiddleware.TruncateMessages(messages, keepLast: 2);

        result.Should().HaveCount(2);
        result.Select(m => m.Text).Should().Equal(["msg4", "msg5"]);
    }

    // IsPromptTooLong unit tests

    [Fact]
    public void IsPromptTooLong_PromptTooLongException_ReturnsTrue()
    {
        var ex = new PromptTooLongException("test");

        PromptTooLongRecoveryRunMiddleware.IsPromptTooLong(ex).Should().BeTrue();
    }

    [Fact]
    public void IsPromptTooLong_InvalidOperationException_ReturnsFalse()
    {
        var ex = new InvalidOperationException("unrelated");

        PromptTooLongRecoveryRunMiddleware.IsPromptTooLong(ex).Should().BeFalse();
    }
}
