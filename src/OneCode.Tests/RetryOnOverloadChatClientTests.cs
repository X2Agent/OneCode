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
            Array.Empty<ChatMessage>(),
            cancellationToken: TestContext.Current.CancellationToken))
        {
            updates.Add(update);
        }

        inner.Attempts.Should().Be(3);
        updates.Should().BeEmpty();
    }

    [Fact]
    public async Task GetStreamingResponseAsync_RethrowsAfterConfiguredRetries()
    {
        var inner = new RateLimitedStreamingClient(failuresBeforeSuccess: 3, CreateRateLimitException);
        using var sut = new RetryOnOverloadChatClient(inner, maxRetries: 2);

        Func<Task> act = async () =>
        {
            await foreach (var _ in sut.GetStreamingResponseAsync(
                Array.Empty<ChatMessage>(),
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
            Array.Empty<ChatMessage>(),
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
            Array.Empty<ChatMessage>(),
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

    private static ClientResultException CreateRateLimitException()
        => new(
            "rate limited",
            new FakePipelineResponse(HttpStatusCode.TooManyRequests, retryAfterSeconds: 1),
            innerException: null);

    private sealed class RateLimitedStreamingClient : IChatClient
    {
        private readonly Func<ClientResultException> _exceptionFactory;
        private int _remainingFailures;

        public RateLimitedStreamingClient(int failuresBeforeSuccess, Func<ClientResultException> exceptionFactory)
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
            return _remainingFailures-- > 0 ? Fail(cancellationToken) : Empty(cancellationToken);
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

        private static async IAsyncEnumerable<ChatResponseUpdate> Empty([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield break;
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

    private static ChatResponseUpdate CreateUpdate(params AIContent[] contents)
        => new(ChatRole.Assistant, contents.ToList());

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

            values = Array.Empty<string>();
            return false;
        }
    }
}
