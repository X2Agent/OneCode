using System.Text.Json;
using Anthropic;
using Anthropic.Core;
using Microsoft.Extensions.AI;
using NSubstitute;
using OneCode.Infrastructure.Ai;
using OneCode.Tests.TestSupport;
using CoreConstants = OneCode.Core.Constants;

namespace OneCode.Tests;

/// <summary>
/// Provider 私有请求参数的 wire 级契约测试。
///
/// 背景：<see cref="ProviderAwareDecorator"/> 曾把 <c>cache_control</c> / <c>thinking</c> /
/// <c>reasoning_effort</c> 写进 <see cref="ChatMessage.AdditionalProperties"/> 与
/// <see cref="ChatOptions.AdditionalProperties"/>——这些不是任何 SDK 的契约，适配器从不读取，
/// 于是 prompt caching 与扩展思考静默失效（单元测试全绿、线上零效果）。
/// 本文件只断言"真正被序列化进 HTTP 请求体的 JSON"，因此无法再被同类回归欺骗。
/// </summary>
public sealed class ProviderWireContractTests
{
    private const string AnthropicResponse = """
        {"id":"msg_1","type":"message","role":"assistant","model":"claude-sonnet-4-5",
         "content":[{"type":"text","text":"ok"}],"stop_reason":"end_turn","stop_sequence":null,
         "usage":{"input_tokens":1,"output_tokens":1}}
        """;

    private const string OpenAiResponse = """
        {"id":"chatcmpl-1","object":"chat.completion","created":1,"model":"gpt-5",
         "choices":[{"index":0,"message":{"role":"assistant","content":"ok"},"finish_reason":"stop"}],
         "usage":{"prompt_tokens":1,"completion_tokens":1,"total_tokens":2}}
        """;

    // OllamaApiClient 的 IChatClient.GetResponseAsync 走流式端点，响应为 NDJSON。
    private const string OllamaResponse = """
        {"model":"llama3.2","created_at":"2024-01-01T00:00:00Z","message":{"role":"assistant","content":"ok"},"done":false}
        {"model":"llama3.2","created_at":"2024-01-01T00:00:01Z","message":{"role":"assistant","content":""},"done":true,"done_reason":"stop"}

        """;

    private static (ProviderAwareDecorator Sut, CapturingHttpHandler Capture) CreateAnthropicSut()
    {
        var capture = new CapturingHttpHandler(AnthropicResponse);
        var options = new ClientOptions
        {
            ApiKey = "test-key",
            BaseUrl = "https://anthropic.invalid",
            Handlers = [new OneCodeIdentityHandler(), capture],
        };
        var client = new AnthropicClient(options).AsIChatClient("claude-sonnet-4-5");
        return (new ProviderAwareDecorator(client, CoreConstants.ModelProviders.Anthropic), capture);
    }

    private static (ProviderAwareDecorator Sut, CapturingHttpHandler Capture) CreateOpenAiSut()
        => CreateHttpSut(OpenAiResponse, CoreConstants.ModelProviders.OpenAI, "gpt-5");

    private static (ProviderAwareDecorator Sut, CapturingHttpHandler Capture) CreateOllamaSut(int? numCtx = null)
        => CreateHttpSut(OllamaResponse, CoreConstants.ModelProviders.Ollama, "llama3.2", numCtx);

    private static (ProviderAwareDecorator Sut, CapturingHttpHandler Capture) CreateHttpSut(
        string response, string providerId, string model, int? numCtx = null)
    {
        var capture = new CapturingHttpHandler(response);
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient(Arg.Any<string>())
            .Returns(_ => new HttpClient(capture) { BaseAddress = new Uri("https://provider.invalid/") });

        var baseClient = new ChatClientFactory(httpClientFactory)
            .CreateBaseClient(providerId, "test-key", "https://provider.invalid", model);
        return (new ProviderAwareDecorator(baseClient, providerId, numCtx), capture);
    }

    private static JsonElement Body(CapturingHttpHandler capture)
        => JsonDocument.Parse(capture.LastRequestBody).RootElement;

    [Fact]
    public async Task Anthropic_PromptCacheBreakpoints_AreSerializedOnContentBlocks()
    {
        var (sut, capture) = CreateAnthropicSut();
        List<ChatMessage> messages =
        [
            new(ChatRole.System, "shared harness"),
            new(ChatRole.User, "hello"),
        ];

        await sut.GetResponseAsync(messages, cancellationToken: TestContext.Current.CancellationToken);

        var body = Body(capture);

        // system 前缀断点：1h TTL
        var systemBlock = body.GetProperty("system")[0];
        systemBlock.GetProperty("text").GetString().Should().Be("shared harness");
        systemBlock.GetProperty("cache_control").GetProperty("type").GetString().Should().Be("ephemeral");
        systemBlock.GetProperty("cache_control").GetProperty("ttl").GetString().Should().Be("1h");

        // 对话前缀断点：默认 5m
        var userBlock = body.GetProperty("messages")[0].GetProperty("content")[0];
        userBlock.GetProperty("text").GetString().Should().Be("hello");
        userBlock.GetProperty("cache_control").GetProperty("type").GetString().Should().Be("ephemeral");
        userBlock.GetProperty("cache_control").GetProperty("ttl").GetString().Should().Be("5m");
    }

    [Fact]
    public async Task Anthropic_Instructions_CarryTheAgentBodyInSystemField()
    {
        var (sut, capture) = CreateAnthropicSut();
        var options = new ChatOptions { Instructions = "agent body" };

        await sut.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], options,
            TestContext.Current.CancellationToken);

        var body = Body(capture);
        body.GetProperty("system")[0].GetProperty("text").GetString().Should().Be("agent body");
        // 正文只应出现一次（在 system 字段里），不能被复制成对话消息
        body.GetProperty("messages").GetArrayLength().Should().Be(1);
        body.GetProperty("messages")[0].GetProperty("role").GetString().Should().Be("user");
    }

    [Fact]
    public async Task Anthropic_NoSystemMessage_MeansNoStaleCacheBreakpoint()
    {
        var (sut, capture) = CreateAnthropicSut();

        await sut.GetResponseAsync([new ChatMessage(ChatRole.User, "only user")], cancellationToken: TestContext.Current.CancellationToken);

        var body = Body(capture);
        // 没有 system 消息时不能凭空合成一个（会与 Instructions 正文重复）
        body.TryGetProperty("system", out _).Should().BeFalse();
        body.GetProperty("messages")[0].GetProperty("content")[0]
            .GetProperty("cache_control").GetProperty("type").GetString().Should().Be("ephemeral");
    }

    [Fact]
    public async Task Anthropic_ReasoningOption_ProducesEnabledThinkingWithABudget()
    {
        var (sut, capture) = CreateAnthropicSut();
        var options = new ChatOptions { Reasoning = new ReasoningOptions { Effort = ReasoningEffort.High } };

        await sut.GetResponseAsync([new ChatMessage(ChatRole.User, "think")], options,
            TestContext.Current.CancellationToken);

        var thinking = Body(capture).GetProperty("thinking");
        thinking.GetProperty("type").GetString().Should().Be("enabled");
        thinking.GetProperty("budget_tokens").GetInt32().Should().Be(16384);
    }

    [Fact]
    public async Task Anthropic_ReasoningOff_ProducesDisabledThinking()
    {
        var (sut, capture) = CreateAnthropicSut();
        var options = new ChatOptions { Reasoning = new ReasoningOptions { Effort = ReasoningEffort.None } };

        await sut.GetResponseAsync([new ChatMessage(ChatRole.User, "think")], options,
            TestContext.Current.CancellationToken);

        Body(capture).GetProperty("thinking").GetProperty("type").GetString().Should().Be("disabled");
    }

    [Fact]
    public async Task Anthropic_NoReasoningOption_ProducesNoThinkingField()
    {
        var (sut, capture) = CreateAnthropicSut();

        await sut.GetResponseAsync([new ChatMessage(ChatRole.User, "plain")], cancellationToken: TestContext.Current.CancellationToken);

        Body(capture).TryGetProperty("thinking", out _).Should().BeFalse();
    }

    [Fact]
    public async Task Anthropic_ThinkingBudget_IsNotClampedAway_WhenMaxOutputTokensIsUnset()
    {
        var (sut, capture) = CreateAnthropicSut();
        var options = new ChatOptions { Reasoning = new ReasoningOptions { Effort = ReasoningEffort.High } };

        await sut.GetResponseAsync([new ChatMessage(ChatRole.User, "think")], options,
            TestContext.Current.CancellationToken);

        var body = Body(capture);
        var budget = body.GetProperty("thinking").GetProperty("budget_tokens").GetInt32();
        // 适配器必须把 max_tokens 抬到 budget 之上，否则请求本身非法 / 输出预算被吃光
        body.GetProperty("max_tokens").GetInt32().Should().BeGreaterThan(budget);
    }

    [Fact]
    public async Task OpenAi_ReasoningOption_IsSerializedAsReasoningEffort()
    {
        var (sut, capture) = CreateOpenAiSut();
        var options = new ChatOptions { Reasoning = new ReasoningOptions { Effort = ReasoningEffort.High } };

        await sut.GetResponseAsync([new ChatMessage(ChatRole.User, "think")], options,
            TestContext.Current.CancellationToken);

        Body(capture).GetProperty("reasoning_effort").GetString().Should().Be("high");
    }

    [Fact]
    public async Task OpenAi_DoesNotSendProviderPrivateCacheOrThinkingKeys()
    {
        var (sut, capture) = CreateOpenAiSut();
        List<ChatMessage> messages =
        [
            new(ChatRole.System, "shared harness"),
            new(ChatRole.User, "hello"),
        ];

        await sut.GetResponseAsync(messages, cancellationToken: TestContext.Current.CancellationToken);

        var body = Body(capture);
        body.TryGetProperty("cache_control", out _).Should().BeFalse();
        body.TryGetProperty("thinking", out _).Should().BeFalse();
        body.TryGetProperty("thinking_budget", out _).Should().BeFalse();
        body.TryGetProperty("reasoning_effort", out _).Should().BeFalse();
    }

    [Fact]
    public async Task Ollama_ReasoningOption_IsSerializedAsNativeThinkOption()
    {
        var (sut, capture) = CreateOllamaSut(numCtx: 8192);
        var options = new ChatOptions { Reasoning = new ReasoningOptions { Effort = ReasoningEffort.Medium } };

        await sut.GetResponseAsync([new ChatMessage(ChatRole.User, "think")], options,
            TestContext.Current.CancellationToken);

        capture.RequestUris[0].AbsolutePath.Should().Be("/api/chat");
        var body = Body(capture);
        // Ollama 原生 API 的 think 是请求体顶层字段，num_ctx 属于 options 子对象
        body.GetProperty("think").GetString().Should().Be("medium");
        body.GetProperty("options").GetProperty("num_ctx").GetInt32().Should().Be(8192);
    }

    [Fact]
    public async Task Ollama_WithoutReasoning_DoesNotSendThinkOption()
    {
        var (sut, capture) = CreateOllamaSut();

        await sut.GetResponseAsync([new ChatMessage(ChatRole.User, "plain")], cancellationToken: TestContext.Current.CancellationToken);

        Body(capture).TryGetProperty("think", out _).Should().BeFalse();
    }

    [Fact]
    public async Task Anthropic_CacheBreakpoints_DoNotAccumulateAcrossCallsOnSharedMessageInstances()
    {
        var (sut, capture) = CreateAnthropicSut();

        // 模拟 MAF 历史层：跨调用复用同一批 ChatMessage / AIContent 实例，逐轮追加新消息
        List<ChatMessage> history =
        [
            new(ChatRole.System, "shared harness"),
            new(ChatRole.User, "turn 1"),
        ];

        await sut.GetResponseAsync(history, cancellationToken: TestContext.Current.CancellationToken);
        history.Add(new ChatMessage(ChatRole.User, [new FunctionResultContent("call_1", "result 1")]));
        await sut.GetResponseAsync(history, cancellationToken: TestContext.Current.CancellationToken);
        history.Add(new ChatMessage(ChatRole.User, [new FunctionResultContent("call_2", "result 2")]));
        await sut.GetResponseAsync(history, cancellationToken: TestContext.Current.CancellationToken);

        capture.RequestBodies.Should().HaveCount(3);
        // 每个请求恰好 2 个断点（system 1h + 末条 5m），不随轮数累积——
        // 超过 4 个 Anthropic 直接拒绝请求（400 invalid_request_error）
        foreach (var body in capture.RequestBodies)
        {
            CountCacheControlBlocks(JsonDocument.Parse(body).RootElement)
                .Should().Be(2, "断点必须每次从干净实例重建，而不是滞留在共享历史里累积");
        }

        // 历史里的原始实例永远不被写入
        history.SelectMany(m => m.Contents)
            .Should().OnlyContain(c => c.AdditionalProperties == null || c.AdditionalProperties.Count == 0);
    }

    private static int CountCacheControlBlocks(JsonElement element)
    {
        var count = element.ValueKind == JsonValueKind.Object && element.TryGetProperty("cache_control", out _)
            ? 1
            : 0;
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                count += element.EnumerateObject().Sum(property => CountCacheControlBlocks(property.Value));
                break;
            case JsonValueKind.Array:
                count += element.EnumerateArray().Sum(CountCacheControlBlocks);
                break;
        }

        return count;
    }

    [Fact]
    public async Task ProviderAwareDecorator_DoesNotMutateTheCallersMessageList()
    {
        var (sut, _) = CreateAnthropicSut();
        List<ChatMessage> messages = [new(ChatRole.User, "hello")];

        await sut.GetResponseAsync(messages, cancellationToken: TestContext.Current.CancellationToken);

        // 装饰器可以给内容块加断点，但不能增删消息（否则重试/回放会看到不同的历史）
        messages.Should().HaveCount(1);
        messages[0].Role.Should().Be(ChatRole.User);
    }
}
