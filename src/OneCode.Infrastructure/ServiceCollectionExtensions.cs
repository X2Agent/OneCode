using Microsoft.Extensions.DependencyInjection;
using OneCode.Core.Ai;
using OneCode.Core.Search;
using OneCode.Infrastructure.Agent;
using OneCode.Infrastructure.Ai;
using OneCode.Infrastructure.Config;
using OneCode.Infrastructure.Search;

namespace OneCode.Infrastructure;

/// <summary>
/// Infrastructure 层 DI 注册扩展方法。
/// 由 App 层组合根调用，将 Infrastructure 层的基础设施服务注册到 DI 容器。
/// </summary>
public static class InfrastructureServiceCollectionExtensions
{
    private static HttpClientHandler CreateTransportHandler()
    {
        var handler = new HttpClientHandler();
        MtlsHelper.ApplyToHandler(handler);
        return handler;
    }

    /// <summary>
    /// Registers Hyperlight CodeAct sandbox service (silent degrade when runtime unavailable).
    /// </summary>
    public static IServiceCollection AddHyperlightCodeAct(this IServiceCollection services)
    {
        services.AddSingleton<IHyperlightRuntimeProbe, HyperlightRuntimeProbe>();
        services.AddSingleton<HyperlightCodeActService>();
        services.AddSingleton<IHyperlightCodeActService>(sp =>
            sp.GetRequiredService<HyperlightCodeActService>());
        return services;
    }

    /// <summary>
    /// Registers named HttpClients used by LLM providers (Ollama / OpenAI-compatible).
    /// Infinite timeouts are intentional for slow local reasoning models.
    /// </summary>
    public static IServiceCollection AddChatHttpClients(this IServiceCollection services)
    {
        services.AddTransient<OpenAiResponseSanitizingHandler>();
        services.AddTransient<OpenAiReasoningPassbackHandler>();
        services.AddTransient<OpenAiRequestDiagnosticsHandler>();
        services.AddTransient<OneCodeIdentityHandler>();

        services.AddHttpClient(Constants.HttpClientNames.Ollama)
            .ConfigurePrimaryHttpMessageHandler(CreateTransportHandler)
            .AddHttpMessageHandler<OneCodeIdentityHandler>()
            .ConfigureHttpClient(static client => client.Timeout = Timeout.InfiniteTimeSpan);

        // Handler order: last AddHttpMessageHandler is outermost.
        // Desired: Identity → Passback → Diagnostics → Sanitizing → Proxy HttpClientHandler
        // 这两个 handler 必须留在 HTTP 层：Passback 注入的 reasoning_content 不在
        // OpenAI SDK 请求模型中（MEAI 转换即丢弃）；Sanitizing 必须在 SDK 反序列化之前
        // 修复退化响应。Diagnostics 放在 Passback 之后，转储的是最终发出的请求体。
        services.AddHttpClient(Constants.HttpClientNames.OpenAI)
            .ConfigurePrimaryHttpMessageHandler(CreateTransportHandler)
            .AddHttpMessageHandler<OpenAiResponseSanitizingHandler>()
            .AddHttpMessageHandler<OpenAiRequestDiagnosticsHandler>()
            .AddHttpMessageHandler<OpenAiReasoningPassbackHandler>()
            .AddHttpMessageHandler<OneCodeIdentityHandler>()
            .ConfigureHttpClient(static client => client.Timeout = Timeout.InfiniteTimeSpan);

        services.AddSingleton<ChatClientFactory>();
        services.AddSingleton<IChatClientFactory>(sp => sp.GetRequiredService<ChatClientFactory>());
        return services;
    }

    /// <summary>
    /// 注册 WebSearch 提供方（当前为 Tavily）。App 层 <c>WebSearchTool</c> 依据
    /// <c>webSearchProvider</c> 设置组装故障转移链（tavily ↔ duckduckgo），
    /// 未配置 API Key 的提供方自动跳过。
    /// </summary>
    public static IServiceCollection AddWebSearchProviders(this IServiceCollection services)
    {
        services.AddSingleton<IWebSearchProvider, TavilySearchProvider>();
        services.AddSingleton<IWebSearchProvider, DuckDuckGoSearchProvider>();
        return services;
    }
}

