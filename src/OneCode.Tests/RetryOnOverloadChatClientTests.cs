using OneCode.Infrastructure.Ai;
using Microsoft.Extensions.AI;
using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net;
using System.Runtime.CompilerServices;

namespace OneCode.Tests;

/// <summary>空 choices 响应检测与上游错误体提取/分类测试。</summary>
public sealed class OpenAiResponseSanitizerEmptyChoicesTests
{
    [Fact]
    public void HasEmptyChoices_EmptyChoicesArray_ReturnsTrue()
    {
        const string body = """{"id":"x","object":"chat.completion","choices":[],"usage":{"total_tokens":0}}""";
        OpenAiResponseSanitizer.HasEmptyChoices(body).Should().BeTrue();
    }

    [Fact]
    public void HasEmptyChoices_MissingChoicesOnChatCompletion_ReturnsTrue()
    {
        const string body = """{"id":"x","object":"chat.completion","created":1}""";
        OpenAiResponseSanitizer.HasEmptyChoices(body).Should().BeTrue();
    }

    [Fact]
    public void HasEmptyChoices_NormalCompletion_ReturnsFalse()
    {
        const string body = """{"id":"x","object":"chat.completion","choices":[{"index":0,"message":{"role":"assistant","content":"hi"},"finish_reason":"stop"}]}""";
        OpenAiResponseSanitizer.HasEmptyChoices(body).Should().BeFalse();
    }

    [Fact]
    public void HasEmptyChoices_ErrorBodyWithoutChoices_ReturnsTrue()
    {
        const string body = """{"error":{"message":"Upstream overloaded","code":503}}""";
        OpenAiResponseSanitizer.HasEmptyChoices(body).Should().BeTrue();
    }

    [Theory]
    [InlineData("<html><body>502 Bad Gateway</body></html>")]
    [InlineData("not json at all")]
    [InlineData("")]
    public void HasEmptyChoices_NonJsonBody_ReturnsFalse(string body)
    {
        // 非 JSON 体不是补全响应，不得误判触发重试。
        OpenAiResponseSanitizer.HasEmptyChoices(body).Should().BeFalse();
    }

    [Fact]
    public void HasEmptyChoices_JsonArrayOrScalarRoot_ReturnsFalse()
    {
        OpenAiResponseSanitizer.HasEmptyChoices("[1,2,3]").Should().BeFalse();
        OpenAiResponseSanitizer.HasEmptyChoices("\"plain string\"").Should().BeFalse();
    }
}

/// <summary>HTTP 200/400 + 显式错误体的提取与瞬时性分类测试。</summary>
public sealed class UpstreamProviderErrorTests
{
    [Theory]
    [InlineData("""{"error":{"message":"Upstream error from Nvidia: Service temporarily overloaded","code":502}}""",
        "Upstream error from Nvidia: Service temporarily overloaded", "502")]
    [InlineData("""{"error":"simple string error"}""", "simple string error", null)]
    [InlineData("""{"error":{"metadata":{"raw":"raw upstream failure"},"code":"overloaded"}}""",
        "raw upstream failure", "overloaded")]
    public void TryExtractUpstreamError_ErrorBody_ParsesMessageAndCode(
        string body, string expectedMessage, string? expectedCode)
    {
        OpenAiResponseSanitizer.TryExtractUpstreamError(body, out var message, out var code)
            .Should().BeTrue();
        message.Should().Be(expectedMessage);
        code.Should().Be(expectedCode);
    }

    [Fact]
    public void TryExtractUpstreamError_OpenRouterMetadataFields_AreFoldedIntoMessage()
    {
        const string body = """{"error":{"message":"openai_error","code":"bad_response_status_code","metadata":{"error_type":"provider_overloaded","provider_code":502}}}""";

        OpenAiResponseSanitizer.TryExtractUpstreamError(body, out var message, out var code)
            .Should().BeTrue();
        code.Should().Be("bad_response_status_code");
        message.Should().Contain("openai_error");
        message.Should().Contain("[error_type=provider_overloaded]");
        message.Should().Contain("[provider_code=502]");
    }

    [Fact]
    public void TryExtractUpstreamError_OpenAiTypeField_IsFoldedIntoMessage()
    {
        const string body = """{"error":{"message":"The server had an error","type":"server_error","code":500}}""";

        OpenAiResponseSanitizer.TryExtractUpstreamError(body, out var message, out _)
            .Should().BeTrue();
        message.Should().Contain("The server had an error");
        message.Should().Contain("[type=server_error]");
    }

    [Theory]
    [InlineData("""{"choices":[{"index":0,"message":{"role":"assistant","content":"hi"}}]}""")]
    [InlineData("not json at all")]
    [InlineData("""{"other":"field"}""")]
    [InlineData("""{"choices":[{"index":0,"message":{"role":"assistant","content":"hi"}}],"error":null}""")]
    [InlineData("""{"choices":[{"index":0,"message":{"role":"assistant","content":"there was an error earlier"}}]}""")]
    [InlineData("""{"choices":[{"index":0,"message":{"role":"assistant","content":"ok"}}],"error":{"message":"ignored","code":500}}""")]
    public void TryExtractUpstreamError_NonErrorBody_ReturnsFalse(string body)
    {
        OpenAiResponseSanitizer.TryExtractUpstreamError(body, out _, out _)
            .Should().BeFalse();
    }

    [Theory]
    [InlineData("502", true)]
    [InlineData("429", true)]
    [InlineData("408", true)]
    [InlineData("401", false)]
    [InlineData("400", false)]
    public void ClassifyTransient_NumericCode_FollowsHttpStatusSemantics(string code, bool expectedTransient)
    {
        UpstreamProviderErrorException.ClassifyTransient(code, "any message").Should().Be(expectedTransient);
    }

    [Fact]
    public void ClassifyTransient_NonNumericCode_UsesMessageKeywords()
    {
        UpstreamProviderErrorException.ClassifyTransient("overloaded", "upstream busy")
            .Should().BeTrue();
        UpstreamProviderErrorException.ClassifyTransient("insufficient_quota", "quota exceeded")
            .Should().BeFalse();
    }

    [Fact]
    public void ClassifyTransient_GatewayRelayError_IsTransientByDefault()
    {
        // 用户实际遇到的故障：网关掩码错误体（HTTP 400 外壳）必须默认瞬时可重试。
        UpstreamProviderErrorException.ClassifyTransient("bad_response_status_code", "openai_error", 400)
            .Should().BeTrue();
        UpstreamProviderErrorException.ClassifyTransient("bad_response_status_code", "openai_error")
            .Should().BeTrue();
    }

    [Theory]
    [InlineData("bad_response_status_code")]
    [InlineData("bad_response")]
    [InlineData("upstream_error")]
    [InlineData("do_request_failed")]
    [InlineData("read_response_body_failed")]
    [InlineData("new_api_error")]
    [InlineData("openai_error")]
    [InlineData("api_error")]
    [InlineData("server_error")]
    [InlineData("internal_error")]
    [InlineData("unmapped")]
    [InlineData("provider_error")]
    [InlineData("provider_overloaded")]
    [InlineData("provider_unavailable")]
    [InlineData("rate_limit_exceeded")]
    [InlineData("timeout")]
    public void ClassifyTransient_GatewayRelayCodes_DefaultToTransient(string code)
    {
        UpstreamProviderErrorException.ClassifyTransient(code, "opaque gateway failure")
            .Should().BeTrue();
    }

    [Theory]
    [InlineData("context_length_exceeded")]
    [InlineData("authentication")]
    [InlineData("payment_required")]
    [InlineData("content_policy_violation")]
    [InlineData("refusal")]
    [InlineData("invalid_request_error")]
    [InlineData("model_not_found")]
    [InlineData("permission_error")]
    [InlineData("not_found_error")]
    [InlineData("insufficient_quota")]
    [InlineData("invalid_api_key")]
    [InlineData("billing_hard_limit_reached")]
    [InlineData("model_not_exists")]
    public void ClassifyTransient_KnownPermanentCodes_FailFast(string code)
    {
        UpstreamProviderErrorException.ClassifyTransient(code, "opaque gateway failure")
            .Should().BeFalse();
    }

    [Fact]
    public void ClassifyTransient_PermanentSignatureBeatsGatewayRelayCode()
    {
        UpstreamProviderErrorException.ClassifyTransient(
                "bad_response_status_code", "Invalid API key provided", 400)
            .Should().BeFalse();
        UpstreamProviderErrorException.ClassifyTransient(
                "openai_error", "This model's maximum context length is 8192 tokens", 400)
            .Should().BeFalse();
    }

    [Fact]
    public void ClassifyTransient_PermanentSignatureMatchesUnderscoredCode()
    {
        UpstreamProviderErrorException.ClassifyTransient("invalid_api_key", "request rejected")
            .Should().BeFalse();
    }

    [Fact]
    public void ClassifyTransient_OpaqueCodeWithTransientHttpStatus_IsTransient()
    {
        UpstreamProviderErrorException.ClassifyTransient("some_unknown_code", "no keywords here", 502)
            .Should().BeTrue();
        UpstreamProviderErrorException.ClassifyTransient("some_unknown_code", "no keywords here", 504)
            .Should().BeTrue();
    }

    [Fact]
    public void ClassifyTransient_UnknownCodeWithPermanentHttpStatus_StaysPermanent()
    {
        UpstreamProviderErrorException.ClassifyTransient("some_unknown_code", "no keywords here", 400)
            .Should().BeFalse();
    }

    [Fact]
    public void Exception_CarriesHttpStatusCode()
    {
        var ex = new UpstreamProviderErrorException(
            "Provider returned HTTP 400 with an upstream error body", "bad_response_status_code", 400);
        ex.HttpStatusCode.Should().Be(400);
        ex.IsTransient.Should().BeTrue();
    }

    [Fact]
    public void Exception_CarriesUpstreamDetails_AndTransientFlag()
    {
        var ex = new UpstreamProviderErrorException(
            "Upstream error from Nvidia: Service temporarily overloaded", "502");
        ex.UpstreamErrorCode.Should().Be("502");
        ex.IsTransient.Should().BeTrue();
        ex.Message.Should().Contain("Nvidia");
    }
}

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
        updates.Should().BeEmpty();
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
        updates.Should().BeEmpty();
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
        updates.Should().BeEmpty();
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

            values = [];
            return false;
        }
    }
}
