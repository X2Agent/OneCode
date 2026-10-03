using OneCode.Infrastructure.Ai;
using Microsoft.Extensions.AI;
using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net;
using System.Runtime.CompilerServices;

namespace OneCode.Tests;

public sealed class RetryOnOverloadChatClientTests
{
    [Fact]
    public async Task GetStreamingResponseAsync_RetriesClientResult429BeforeFirstChunk()
    {
        var inner = new RateLimitedStreamingClient(failuresBeforeSuccess: 2, CreateRateLimitException);
        using var sut = new RetryOnOverloadChatClient(inner, maxRetries: 3);

        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in sut.GetStreamingResponseAsync(
            [],
            cancellationToken: TestContext.Current.CancellationToken))
        {
            updates.Add(update);
        }

        inner.Attempts.Should().Be(3);
        ConcatText(updates).Should().Be("ok");
    }

    [Fact]
    public async Task GetStreamingResponseAsync_RetriesEmptyChoicesExceptionBeforeFirstChunk()
    {
        // 免费模型过载时返回 HTTP 200 + 空 choices（body 内嵌 502 错误），
        // OpenAiResponseSanitizingHandler 将其转为 EmptyChoicesResponseException。
        // 流式路径必须与非流式路径一致地按瞬时上游错误重试，而不是直接抛给上层。
        var inner = new RateLimitedStreamingClient(
            failuresBeforeSuccess: 1,
            () => new EmptyChoicesResponseException(
                "Provider returned HTTP 200 with no completion choices. Body preview: {\"error\":{\"code\":502}}"));
        using var sut = new RetryOnOverloadChatClient(inner, maxRetries: 2);

        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in sut.GetStreamingResponseAsync(
            [],
            cancellationToken: TestContext.Current.CancellationToken))
        {
            updates.Add(update);
        }

        inner.Attempts.Should().Be(2);
        ConcatText(updates).Should().Be("ok");
    }

    [Fact]
    public async Task GetStreamingResponseAsync_RetriesTransientUpstreamErrorBodyAndRecovers()
    {
        // 上游过载错误体（Nvidia 502）被 Handler 转为瞬时的 UpstreamProviderErrorException，
        // 流式路径应与非流式路径一致地自动重试。
        var inner = new RateLimitedStreamingClient(
            failuresBeforeSuccess: 1,
            () => new UpstreamProviderErrorException(
                "Upstream error from Nvidia: Service temporarily overloaded", "502"));
        using var sut = new RetryOnOverloadChatClient(inner, maxRetries: 2);

        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in sut.GetStreamingResponseAsync(
            [],
            cancellationToken: TestContext.Current.CancellationToken))
        {
            updates.Add(update);
        }

        inner.Attempts.Should().Be(2);
        ConcatText(updates).Should().Be("ok");
    }

    [Fact]
    public async Task GetStreamingResponseAsync_RetriesZeroContentEmptyStreamAndRecovers()
    {
        // 2026-09 事故:OpenRouter 实验模型返回 HTTP 200 + 零内容 SSE,流"正常"结束不抛异常,
        // 旧逻辑静默按 Completed 收尾(TUI 零输出零提示)。空流必须按瞬时错误重试。
        var inner = new EmptyStreamClient(emptyStreamsBeforeContent: 1);
        using var sut = new RetryOnOverloadChatClient(inner, maxRetries: 3);

        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in sut.GetStreamingResponseAsync(
            [],
            cancellationToken: TestContext.Current.CancellationToken))
        {
            updates.Add(update);
        }

        inner.Attempts.Should().Be(2);
        ConcatText(updates).Should().Be("ok");
    }

    [Fact]
    public async Task GetStreamingResponseAsync_ThrowsEmptyChoicesWhenStreamNeverCarriesContent()
    {
        // 重试预算耗尽后流仍零内容 → 抛 EmptyChoicesResponseException,
        // 沿 QueryStreamEngine 异常路径在 TUI 显示错误,不允许再静默收尾。
        var inner = new EmptyStreamClient(emptyStreamsBeforeContent: int.MaxValue);
        using var sut = new RetryOnOverloadChatClient(inner, maxRetries: 1);

        Func<Task> act = async () =>
        {
            await foreach (var _ in sut.GetStreamingResponseAsync(
                [],
                cancellationToken: TestContext.Current.CancellationToken))
            {
            }
        };

        await act.Should().ThrowAsync<EmptyChoicesResponseException>();
        inner.Attempts.Should().Be(2);
    }

    [Fact]
    public async Task GetStreamingResponseAsync_UsageOnlyStream_IsTreatedAsZeroContent()
    {
        // 只含 usage 统计的流不是有效输出——事故现场的签名就是 token 有计数、内容为零。
        var inner = new EmptyStreamClient(emptyStreamsBeforeContent: 1, emitUsageOnly: true);
        using var sut = new RetryOnOverloadChatClient(inner, maxRetries: 3);

        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in sut.GetStreamingResponseAsync(
            [],
            cancellationToken: TestContext.Current.CancellationToken))
        {
            updates.Add(update);
        }

        inner.Attempts.Should().Be(2);
        ConcatText(updates).Should().Be("ok");
    }

    [Fact]
    public async Task GetStreamingResponseAsync_DoesNotRetryPermanentUpstreamError()
    {
        // 401 无效密钥属于永久错误——立即失败，不浪费重试预算。
        var inner = new RateLimitedStreamingClient(
            failuresBeforeSuccess: 5,
            () => new UpstreamProviderErrorException("Invalid API key provided", "401"));
        using var sut = new RetryOnOverloadChatClient(inner, maxRetries: 3);

        Func<Task> act = async () =>
        {
            await foreach (var _ in sut.GetStreamingResponseAsync(
                [],
                cancellationToken: TestContext.Current.CancellationToken))
            {
            }
        };

        await act.Should().ThrowAsync<UpstreamProviderErrorException>()
            .Where(ex => ex.Message.Contains("Invalid API key"));
        inner.Attempts.Should().Be(1);
    }

    [Fact]
    public async Task GetStreamingResponseAsync_RethrowsAfterConfiguredRetries()
    {
        var inner = new RateLimitedStreamingClient(failuresBeforeSuccess: 3, CreateRateLimitException);
        using var sut = new RetryOnOverloadChatClient(inner, maxRetries: 2);

        Func<Task> act = async () =>
        {
            await foreach (var _ in sut.GetStreamingResponseAsync(
                [],
                cancellationToken: TestContext.Current.CancellationToken))
            {
            }
        };

        await act.Should().ThrowAsync<ClientResultException>();
        inner.Attempts.Should().Be(3);
    }

    [Fact]
    public async Task GetStreamingResponseAsync_Retries429AfterTextChunkWithoutDuplicatingOutput()
    {
        var inner = new MidStreamTextRateLimitedClient(CreateRateLimitException);
        using var sut = new RetryOnOverloadChatClient(inner, maxRetries: 3);

        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in sut.GetStreamingResponseAsync(
            [],
            cancellationToken: TestContext.Current.CancellationToken))
        {
            updates.Add(update);
        }

        inner.Attempts.Should().Be(2);
        string.Concat(updates
                .SelectMany(static update => update.Contents)
                .OfType<TextContent>()
                .Select(static text => text.Text))
            .Should().Be("Hello world");
    }

    [Fact]
    public async Task GetStreamingResponseAsync_Retries429AfterToolCallWithoutDuplicatingToolUse()
    {
        var inner = new MidStreamToolRateLimitedClient(CreateRateLimitException);
        using var sut = new RetryOnOverloadChatClient(inner, maxRetries: 3);

        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in sut.GetStreamingResponseAsync(
            [],
            cancellationToken: TestContext.Current.CancellationToken))
        {
            updates.Add(update);
        }

        inner.Attempts.Should().Be(2);
        updates
            .SelectMany(static update => update.Contents)
            .OfType<FunctionCallContent>()
            .Should().ContainSingle(call => call.CallId == "call-1" && call.Name == "Read");
    }

    [Fact]
    public async Task GetStreamingResponseAsync_RetriesStalledStreamAfterFirstChunkAndRecovers()
    {
        // 2026-09 事故:HttpClient.Timeout = Infinite 下 TCP 停滞表现为"流不结束也不抛错",
        // 空响应防线(仅流正常结束时触发)覆盖不到,TUI 永久显示"执行 <工具>"。
        // 首个 chunk 之后无新数据须按停滞取消本次尝试并重试,replay 去重不重复输出。
        var inner = new StallAfterFirstChunkClient();
        using var sut = new RetryOnOverloadChatClient(
            inner, maxRetries: 3, streamIdleTimeout: TimeSpan.FromMilliseconds(150));

        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in sut.GetStreamingResponseAsync(
            [],
            cancellationToken: TestContext.Current.CancellationToken))
        {
            updates.Add(update);
        }

        inner.Attempts.Should().Be(2);
        ConcatText(updates).Should().Be("Hello world");
    }

    [Fact]
    public async Task GetStreamingResponseAsync_ThrowsStalledExceptionWhenStallPersists()
    {
        var inner = new AlwaysStallingClient();
        using var sut = new RetryOnOverloadChatClient(
            inner, maxRetries: 1, streamIdleTimeout: TimeSpan.FromMilliseconds(100));

        Func<Task> act = async () =>
        {
            await foreach (var _ in sut.GetStreamingResponseAsync(
                [],
                cancellationToken: TestContext.Current.CancellationToken))
            {
            }
        };

        await act.Should().ThrowAsync<StreamStalledException>();
        inner.Attempts.Should().Be(2);
    }

    [Fact]
    public async Task GetStreamingResponseAsync_UserCancellationDuringStallPropagatesAsCancelled()
    {
        // 空闲取消与用户取消必须可区分:用户 Esc 中断走原始取消令牌,不得被改写成停滞异常。
        var inner = new AlwaysStallingClient();
        using var sut = new RetryOnOverloadChatClient(
            inner, maxRetries: 3, streamIdleTimeout: TimeSpan.FromSeconds(30));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        Func<Task> act = async () =>
        {
            await foreach (var _ in sut.GetStreamingResponseAsync(
                [],
                cancellationToken: cts.Token))
            {
            }
        };

        await act.Should().ThrowAsync<OperationCanceledException>();
        inner.Attempts.Should().Be(1);
    }

    private static ClientResultException CreateRateLimitException()
        => new(
            "rate limited",
            new FakePipelineResponse(HttpStatusCode.TooManyRequests, retryAfterSeconds: 1),
            innerException: null);

    private sealed class RateLimitedStreamingClient : IChatClient
    {
        private readonly Func<Exception> _exceptionFactory;
        private int _remainingFailures;

        public RateLimitedStreamingClient(int failuresBeforeSuccess, Func<Exception> exceptionFactory)
        {
            _remainingFailures = failuresBeforeSuccess;
            _exceptionFactory = exceptionFactory;
        }

        public int Attempts { get; private set; }

        public void Dispose()
        {
        }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Attempts++;
            return _remainingFailures-- > 0 ? Fail(cancellationToken) : SuccessWithText(cancellationToken);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        private async IAsyncEnumerable<ChatResponseUpdate> Fail([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            throw _exceptionFactory();
#pragma warning disable CS0162
            yield break;
#pragma warning restore CS0162
        }

        private static async IAsyncEnumerable<ChatResponseUpdate> SuccessWithText([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield return CreateUpdate(new TextContent("ok"));
        }
    }

    /// <summary>流"成功"结束但零内容(纯空流或仅 usage)——复现 OpenRouter 实验模型的空响应。</summary>
    private sealed class EmptyStreamClient(int emptyStreamsBeforeContent, bool emitUsageOnly = false) : IChatClient
    {
        private int _attempts;

        public int Attempts => _attempts;

        public void Dispose()
        {
        }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            _attempts++;
            return _attempts <= emptyStreamsBeforeContent
                ? (emitUsageOnly ? UsageOnly(cancellationToken) : Nothing(cancellationToken))
                : TextOnly(cancellationToken);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        private static async IAsyncEnumerable<ChatResponseUpdate> Nothing(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield break;
        }

        private static async IAsyncEnumerable<ChatResponseUpdate> UsageOnly(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield return CreateUpdate(new UsageContent(new UsageDetails
            {
                InputTokenCount = 10,
                OutputTokenCount = 0,
            }));
        }

        private static async IAsyncEnumerable<ChatResponseUpdate> TextOnly(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield return CreateUpdate(new TextContent("ok"));
        }
    }

    private sealed class MidStreamTextRateLimitedClient : IChatClient
    {
        private readonly Func<ClientResultException> _exceptionFactory;

        public MidStreamTextRateLimitedClient(Func<ClientResultException> exceptionFactory)
        {
            _exceptionFactory = exceptionFactory;
        }

        public int Attempts { get; private set; }

        public void Dispose()
        {
        }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Attempts++;
            return Attempts == 1
                ? StreamThenFail(cancellationToken)
                : ReplayThenComplete(cancellationToken);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        private async IAsyncEnumerable<ChatResponseUpdate> StreamThenFail([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return CreateUpdate(new TextContent("Hello"));
            await Task.Yield();
            throw _exceptionFactory();
#pragma warning disable CS0162
            yield break;
#pragma warning restore CS0162
        }

        private static async IAsyncEnumerable<ChatResponseUpdate> ReplayThenComplete([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return CreateUpdate(new TextContent("Hello "));
            await Task.Yield();
            yield return CreateUpdate(new TextContent("world"));
        }
    }

    private sealed class MidStreamToolRateLimitedClient : IChatClient
    {
        private readonly Func<ClientResultException> _exceptionFactory;

        public MidStreamToolRateLimitedClient(Func<ClientResultException> exceptionFactory)
        {
            _exceptionFactory = exceptionFactory;
        }

        public int Attempts { get; private set; }

        public void Dispose()
        {
        }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Attempts++;
            return Attempts == 1
                ? ToolThenFail(cancellationToken)
                : ReplayToolThenComplete(cancellationToken);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        private async IAsyncEnumerable<ChatResponseUpdate> ToolThenFail([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return CreateUpdate(CreateToolCall("call-1"));
            await Task.Yield();
            throw _exceptionFactory();
#pragma warning disable CS0162
            yield break;
#pragma warning restore CS0162
        }

        private static async IAsyncEnumerable<ChatResponseUpdate> ReplayToolThenComplete([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return CreateUpdate(CreateToolCall("call-2"));
            await Task.Yield();
        }
    }

    /// <summary>首个 chunk 后连接停滞（不断开、无新数据，直到尝试级取消触发）——复现 TCP 半开场景。</summary>
    private sealed class StallAfterFirstChunkClient : IChatClient
    {
        public int Attempts { get; private set; }

        public void Dispose()
        {
        }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Attempts++;
            return Attempts == 1
                ? StallAfterFirstChunk(cancellationToken)
                : ReplayFullText(cancellationToken);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        private static async IAsyncEnumerable<ChatResponseUpdate> StallAfterFirstChunk(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return CreateUpdate(new TextContent("Hel"));
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            yield break;
        }

        private static async IAsyncEnumerable<ChatResponseUpdate> ReplayFullText(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return CreateUpdate(new TextContent("Hel"));
            await Task.Yield();
            yield return CreateUpdate(new TextContent("lo world"));
        }
    }

    /// <summary>每次尝试都在首个 chunk 后停滞——重试预算耗尽须抛 StreamStalledException。</summary>
    private sealed class AlwaysStallingClient : IChatClient
    {
        public int Attempts { get; private set; }

        public void Dispose()
        {
        }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Attempts++;
            return StallAfterFirstChunkClientStall(cancellationToken);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        private static async IAsyncEnumerable<ChatResponseUpdate> StallAfterFirstChunkClientStall(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return CreateUpdate(new TextContent("Hel"));
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            yield break;
        }
    }

    private static ChatResponseUpdate CreateUpdate(params AIContent[] contents)
        => new(ChatRole.Assistant, contents.ToList());

    private static string ConcatText(IEnumerable<ChatResponseUpdate> updates)
        => string.Concat(updates
            .SelectMany(static update => update.Contents)
            .OfType<TextContent>()
            .Select(static text => text.Text));

    private static FunctionCallContent CreateToolCall(string callId)
        => new(callId, "Read", new Dictionary<string, object?>
        {
            ["filePath"] = "demo.txt",
        });

    private sealed class FakePipelineResponse : PipelineResponse
    {
        private readonly PipelineResponseHeaders _headers;
        private bool _isError;

        public FakePipelineResponse(HttpStatusCode status, int retryAfterSeconds)
        {
            Status = (int)status;
            ReasonPhrase = status.ToString();
            _headers = new FakePipelineResponseHeaders(new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
            {
                ["Retry-After"] = [retryAfterSeconds.ToString(CultureInfo.InvariantCulture)],
            });
            _isError = true;
        }

        public override BinaryData Content => BinaryData.FromString("{}");

        public override Stream? ContentStream { get; set; }

        protected override PipelineResponseHeaders HeadersCore
        {
            get => _headers;
        }

        public override bool IsError => _isError;

        protected override bool IsErrorCore
        {
            get => _isError;
            set => _isError = value;
        }

        public override string ReasonPhrase { get; }

        public override int Status { get; }

        public override void Dispose()
        {
        }

        public override BinaryData BufferContent(CancellationToken cancellationToken = default)
            => Content;

        public override ValueTask<BinaryData> BufferContentAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromResult(Content);
    }

    private sealed class FakePipelineResponseHeaders : PipelineResponseHeaders
    {
        private readonly IReadOnlyDictionary<string, string[]> _headers;

        public FakePipelineResponseHeaders(IReadOnlyDictionary<string, string[]> headers)
        {
            _headers = headers;
        }

        public override IEnumerator<KeyValuePair<string, string>> GetEnumerator()
        {
            foreach (var header in _headers)
            {
                yield return new KeyValuePair<string, string>(header.Key, string.Join(",", header.Value));
            }
        }

        public override bool TryGetValue(string name, out string value)
        {
            if (_headers.TryGetValue(name, out var values))
            {
                value = values[0];
                return true;
            }

            value = string.Empty;
            return false;
        }

        public override bool TryGetValues(string name, out IEnumerable<string> values)
        {
            if (_headers.TryGetValue(name, out var stored))
            {
                values = stored;
                return true;
            }

            values = [];
            return false;
        }
    }
}
