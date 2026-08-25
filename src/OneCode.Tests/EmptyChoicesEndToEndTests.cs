using System.ClientModel;
using System.Net;
using Microsoft.Extensions.AI;
using OneCode.Infrastructure.Ai;

namespace OneCode.Tests;

/// <summary>
/// 端到端机制验证：真实的 OpenAI SDK 反序列化管线（HttpClientPipelineTransport →
/// OpenAI SDK → Microsoft.Extensions.AI）+ OpenAiResponseSanitizingHandler。
///
/// 验证三件事：
/// 1. 无 Sanitizer 时，200 + 空 choices 响应体确实会让 SDK 在
///    ChatCompletion.get_Role() 内部抛 ArgumentOutOfRangeException（原始故障模式复现）；
/// 2. 挂上 Sanitizer 后，同一响应体被拦截为 EmptyChoicesResponseException（修复生效）；
/// 3. RetryOnOverloadChatClient 把 EmptyChoicesResponseException 视为瞬时错误自动重试，
///    下一次返回正常响应时成功恢复。
/// </summary>
public sealed class EmptyChoicesEndToEndTests
{
    private const string ValidCompletionBody =
        """{"id":"1","object":"chat.completion","created":1,"model":"m","choices":[{"index":0,"message":{"role":"assistant","content":"ok"},"finish_reason":"stop"}],"usage":{"prompt_tokens":1,"completion_tokens":1,"total_tokens":2}}""";

    private const string EmptyChoicesBody =
        """{"id":"1","object":"chat.completion","created":1,"model":"m","choices":[],"usage":{"prompt_tokens":0,"completion_tokens":0,"total_tokens":0}}""";

    [Fact]
    public async Task WithoutSanitizer_EmptyChoicesCrashesSdkWithArgumentOutOfRange()
    {
        using var client = CreateHttpClient(new StaticBodyHandler(EmptyChoicesBody), sanitizing: false);
        var chatClient = CreateSdkChatClient(client);

        Func<Task> act = async () => await chatClient.GetResponseAsync(
            "hi", cancellationToken: TestContext.Current.CancellationToken);

        // 原始故障模式：SDK 内部索引越界，而非有意义的错误。
        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task WithSanitizer_EmptyChoicesBecomesEmptyChoicesResponseException()
    {
        using var client = CreateHttpClient(new StaticBodyHandler(EmptyChoicesBody), sanitizing: true);
        var chatClient = CreateSdkChatClient(client);

        Func<Task> act = async () => await chatClient.GetResponseAsync(
            "hi", cancellationToken: TestContext.Current.CancellationToken);

        var thrown = await act.Should().ThrowAsync<EmptyChoicesResponseException>();
        thrown.Which.Message.Should().Contain("choices");
    }

    [Fact]
    public async Task RetryOnOverload_RetriesEmptyChoicesAndRecoversOnNextAttempt()
    {
        var handler = new StaticBodyHandler(EmptyChoicesBody, ValidCompletionBody);
        using var client = CreateHttpClient(handler, sanitizing: true);
        var inner = CreateSdkChatClient(client);
        using var sut = new RetryOnOverloadChatClient(inner, maxRetries: 3);

        var response = await sut.GetResponseAsync(
            "hi", cancellationToken: TestContext.Current.CancellationToken);

        response.Text.Should().Be("ok");
        handler.RequestCount.Should().Be(2);
    }

    [Fact]
    public async Task WithSanitizer_UpstreamErrorBodyBecomesUpstreamProviderErrorException()
    {
        // Nvidia 过载场景：HTTP 200 + {"error":{"message":"...","code":502}}，
        // 异常必须保留上游真实消息与错误码（而非笼统的 empty choices），且判定为瞬时。
        const string errorBody =
            """{"error":{"message":"Upstream error from Nvidia: Service temporarily overloaded","code":502}}""";
        using var client = CreateHttpClient(new StaticBodyHandler(errorBody), sanitizing: true);
        var chatClient = CreateSdkChatClient(client);

        Func<Task> act = async () => await chatClient.GetResponseAsync(
            "hi", cancellationToken: TestContext.Current.CancellationToken);

        var thrown = await act.Should().ThrowAsync<UpstreamProviderErrorException>();
        thrown.Which.Message.Should().Contain("Nvidia");
        thrown.Which.UpstreamErrorCode.Should().Be("502");
        thrown.Which.IsTransient.Should().BeTrue();
    }

    [Fact]
    public async Task WithSanitizer_PermanentUpstreamErrorBodyIsNotRetried()
    {
        // 401 属于永久错误——即使 RetryOnOverloadChatClient 在链路上也只请求一次。
        const string errorBody =
            """{"error":{"message":"Invalid API key provided","code":401}}""";
        var handler = new StaticBodyHandler(errorBody);
        using var client = CreateHttpClient(handler, sanitizing: true);
        var inner = CreateSdkChatClient(client);
        using var sut = new RetryOnOverloadChatClient(inner, maxRetries: 3);

        Func<Task> act = async () => await sut.GetResponseAsync(
            "hi", cancellationToken: TestContext.Current.CancellationToken);

        var thrown = await act.Should().ThrowAsync<UpstreamProviderErrorException>();
        thrown.Which.IsTransient.Should().BeFalse();
        handler.RequestCount.Should().Be(1);
    }

    [Fact]
    public async Task WithSanitizer_NormalCompletionWithErrorNullField_PassesThroughUntouched()
    {
        // 某些网关在正常补全中附带 "error": null —— 必须原样通过，绝不能误判为错误体。
        const string body =
            """{"id":"1","object":"chat.completion","created":1,"model":"m","choices":[{"index":0,"message":{"role":"assistant","content":"ok"},"finish_reason":"stop"}],"error":null,"usage":{"prompt_tokens":1,"completion_tokens":1,"total_tokens":2}}""";
        using var client = CreateHttpClient(new StaticBodyHandler(body), sanitizing: true);
        var chatClient = CreateSdkChatClient(client);

        var response = await chatClient.GetResponseAsync(
            "hi", cancellationToken: TestContext.Current.CancellationToken);

        response.Text.Should().Be("ok");
    }

    private static HttpClient CreateHttpClient(HttpMessageHandler handler, bool sanitizing)
    {
        HttpMessageHandler pipeline = sanitizing
            ? new OpenAiResponseSanitizingHandler { InnerHandler = handler }
            : handler;
        return new HttpClient(pipeline);
    }

    private static IChatClient CreateSdkChatClient(HttpClient httpClient)
    {
        var options = new OpenAI.OpenAIClientOptions
        {
            Endpoint = new Uri("http://localhost:59999/v1"),
            Transport = new System.ClientModel.Primitives.HttpClientPipelineTransport(httpClient),
        };
        var openAiClient = new OpenAI.OpenAIClient(new ApiKeyCredential("test-key"), options);
        return openAiClient.GetChatClient("test-model").AsIChatClient();
    }

    /// <summary>按序返回预设响应体的 stub handler（超出后重复最后一个）。</summary>
    private sealed class StaticBodyHandler(params string[] bodies) : HttpMessageHandler
    {
        private int _index;

        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            var body = _index < bodies.Length ? bodies[_index] : bodies[^1];
            _index++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
            });
        }
    }
}
