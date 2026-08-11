using Anthropic;
using Anthropic.Core;
using Microsoft.Extensions.AI;
using OllamaSharp;
using OneCode.Infrastructure.Config;
using System.ClientModel;
using System.ClientModel.Primitives;
using CoreConstants = OneCode.Core.Constants;

namespace OneCode.Infrastructure.Ai;

/// <summary>
/// AI 客户端工厂：根据 provider 标识创建具体的 IChatClient 实现。
/// Ollama / OpenAI 路径通过 <see cref="IHttpClientFactory"/> 获取命名客户端，禁止直接 <c>new HttpClient</c>。
/// Anthropic SDK 使用自有 Handler 列表（仍注入 <see cref="OneCodeIdentityHandler"/>）。
/// </summary>
public sealed class ChatClientFactory(IHttpClientFactory httpClientFactory) : IChatClientFactory
{
    /// <summary>
    /// 创建底层 IChatClient（Anthropic / OpenAI / 原生 Ollama）。
    /// 不含装饰器链（RetryOnOverload / ProviderAware / MaxOutputTokens），
    /// 调用方负责按需组装。
    /// </summary>
    public IChatClient CreateBaseClient(
        string providerId,
        string apiKey,
        string? baseUrl = null,
        string? model = null,
        ILoggerFactory? loggerFactory = null,
        int? ollamaNumCtx = null)
    {
        return providerId switch
        {
            CoreConstants.ModelProviders.Anthropic => CreateAnthropicClient(apiKey, baseUrl, model),
            CoreConstants.ModelProviders.Ollama => CreateOllamaClient(baseUrl, model),
            _ => CreateOpenAiClient(apiKey, baseUrl, model),
        };
    }

    /// <summary>
    /// 组装完整的 IChatClient 装饰器链：
    /// VcrChatClient → ProviderAware → RetryOnOverload → baseClient
    /// </summary>
    public IChatClient CreateWithDecorators(
        string providerId,
        string apiKey,
        string? baseUrl = null,
        string? model = null,
        ILoggerFactory? loggerFactory = null,
        VcrMode? vcrMode = null,
        int? ollamaNumCtx = null)
    {
        var baseClient = CreateBaseClient(providerId, apiKey, baseUrl, model, loggerFactory, ollamaNumCtx);

        var retryLogger = loggerFactory?.CreateLogger<RetryOnOverloadChatClient>();
        var retryClient = new RetryOnOverloadChatClient(baseClient, retryLogger);
        var providerAware = new ProviderAwareDecorator(retryClient, providerId, ollamaNumCtx);

        return vcrMode is { } m && m.IsActive()
            ? new VcrChatClientDecorator(providerAware, m)
            : providerAware;
    }

    private static IChatClient CreateAnthropicClient(string apiKey, string? baseUrl, string? model)
    {
        var effectiveBaseUrl = baseUrl?.TrimEnd('/');
        // Anthropic SDK appends /v1 internally, so strip it if the user included it.
        if (effectiveBaseUrl != null && effectiveBaseUrl.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
            effectiveBaseUrl = effectiveBaseUrl[..^3];

        var options = new ClientOptions
        {
            ApiKey = apiKey,
            Handlers = [new OneCodeIdentityHandler()],
        };
        if (!string.IsNullOrEmpty(effectiveBaseUrl))
            options.BaseUrl = effectiveBaseUrl;

        var client = new AnthropicClient(options);
        return client.AsIChatClient(model);
    }

    private IChatClient CreateOllamaClient(string? baseUrl, string? model)
    {
        var httpClient = httpClientFactory.CreateClient(Constants.HttpClientNames.Ollama);
        httpClient.BaseAddress = new Uri(NormalizeOllamaEndpoint(baseUrl));

        // OllamaApiClient implements IChatClient and uses /api/chat, preserving
        // native fields such as think and message.thinking during streaming.
        return new OllamaApiClient(httpClient, model ?? "llama3.2");
    }

    private IChatClient CreateOpenAiClient(
        string apiKey,
        string? baseUrl = null,
        string? model = null)
    {
        var defaultModel = model ?? "gpt-4o";
        var credential = new ApiKeyCredential(apiKey);

        var httpClient = httpClientFactory.CreateClient(Constants.HttpClientNames.OpenAI);
        var transport = new HttpClientPipelineTransport(httpClient);

        // OpenAIClientOptions.NetworkTimeout is separate from HttpClient.Timeout —
        // both must stay infinite for slow local reasoning models.
        var options = new OpenAI.OpenAIClientOptions
        {
            Transport = transport,
            NetworkTimeout = Timeout.InfiniteTimeSpan,
        };
        if (!string.IsNullOrEmpty(baseUrl))
            options.Endpoint = new Uri(baseUrl);

        var openAiClient = new OpenAI.OpenAIClient(credential, options);
        var nativeChatClient = openAiClient.GetChatClient(defaultModel);
        return nativeChatClient.AsIChatClient();
    }

    private static string NormalizeOllamaEndpoint(string? baseUrl)
    {
        var endpoint = string.IsNullOrWhiteSpace(baseUrl)
            ? "http://localhost:11434"
            : baseUrl.TrimEnd('/');

        // Ollama native API targets the server root (/api/chat), not /v1.
        if (endpoint.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
            endpoint = endpoint[..^3];

        return endpoint.TrimEnd('/') + "/";
    }
}
