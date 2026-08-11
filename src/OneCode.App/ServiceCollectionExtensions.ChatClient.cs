using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using OneCode.App.Query;
using OneCode.Infrastructure;
using OneCode.Infrastructure.Ai;
using OneCode.Infrastructure.Config;
using CoreConstants = OneCode.Core.Constants;

namespace OneCode.App;

public static partial class ServiceCollectionExtensions
{
    /// <summary>
    /// Ollama 不需要认证，apiKey 缺失时用此占位符绕过 ApiKeyCredential 的非空约束。
    /// </summary>
    private const string OllamaNoAuthPlaceholder = "ollama-no-auth";

    /// <summary>
    /// 注册 <see cref="IChatClient"/>，配置在首次解析时从 <see cref="IConfigManager"/> 读取。
    ///
    /// 配置来源和优先级由 <see cref="IConfigManager.Current"/> 统一解析；本注册点只消费有效快照。
    ///
    /// SDK 特化代码（Anthropic.SDK / OpenAI SDK / 装饰器链）已下沉到
    /// <see cref="ChatClientFactory"/>，本方法仅负责 DI 注册。
    ///
    /// apiKey 处理策略：
    ///   - Anthropic / OpenAI：apiKey 为空时注册 <see cref="MissingApiKeyChatClient"/> 哨兵，
    ///     首次调用时抛出可操作错误，避免应用启动即崩溃。
    ///   - Ollama：不需要认证，apiKey 为空时用占位符继续，保证应用正常启动。
    /// </summary>
    public static IServiceCollection RegisterChatClient(this IServiceCollection services)
    {
        services.AddChatHttpClients();

        services.AddSingleton<IChatClient>(sp =>
        {
            var configManager = sp.GetRequiredService<IConfigManager>();
            var settings = configManager.Current.Effective;
            var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
            var chatClientFactory = sp.GetRequiredService<IChatClientFactory>();

            // ConfigManager 已统一解析 Environment > Project > User > BuiltIn。
            var apiKey = settings.ApiKey;
            var baseUrl = settings.BaseUrl;
            var model = settings.Model;
            var providerId = string.IsNullOrEmpty(settings.Provider)
                ? CoreConstants.ModelProviders.Anthropic
                : settings.Provider.ToLowerInvariant();

            var requiresAuth = !string.Equals(
                providerId, CoreConstants.ModelProviders.Ollama, StringComparison.OrdinalIgnoreCase);

            if (requiresAuth && string.IsNullOrEmpty(apiKey))
            {
                var logger = loggerFactory.CreateLogger<MissingApiKeyChatClient>();
                return new MissingApiKeyChatClient(logger);
            }

            var effectiveApiKey = apiKey ?? OllamaNoAuthPlaceholder;
            // VCR 装饰器：未注册或未激活时工厂内部跳过，零影响生产路径。
            var vcrMode = sp.GetService<VcrMode>();
            var isOllama = string.Equals(
                providerId, CoreConstants.ModelProviders.Ollama, StringComparison.OrdinalIgnoreCase);

            var ollamaNumCtx = isOllama ? settings.OllamaContextWindow : (int?)null;
            return chatClientFactory.CreateWithDecorators(
                providerId, effectiveApiKey, baseUrl, model, loggerFactory, vcrMode, ollamaNumCtx);
        });

        return services;
    }
}
