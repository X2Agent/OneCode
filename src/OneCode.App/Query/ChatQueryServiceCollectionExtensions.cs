using Microsoft.Extensions.DependencyInjection;
using OneCode.App.Services;
using OneCode.App.Services.Agent;
using OneCode.App.Services.Compact;
using OneCode.App.Services.PlanMode;

namespace OneCode.App.Query;

/// <summary>
/// Chat 查询领域 DI 注册——与查询实现（<see cref="QueryStreamEngine"/>）同目录维护。
/// 由组合根 <see cref="OneCode.App.OneCodeApp"/> 显式调用。
/// </summary>
public static class ChatQueryServiceCollectionExtensions
{
    public static IServiceCollection AddChatQueryServices(this IServiceCollection services)
    {
        services.AddSingleton<ChatSessionDependencies>();
        services.AddSingleton<ChatObservabilityDependencies>();
        services.AddSingleton<IToolProtocolValidator, ToolProtocolValidator>();
        services.AddSingleton<QueryStreamEngine>(sp => new QueryStreamEngine(
            sp.GetRequiredService<ILogger<QueryStreamEngine>>(),
            sp.GetRequiredService<IMainAgentRunner>(),
            sp.GetRequiredService<IToolCatalog>(),
            sp.GetRequiredService<IHookExecutionService>(),
            sp.GetRequiredService<ChatSessionDependencies>(),
            sp.GetRequiredService<ChatObservabilityDependencies>(),
            sp.GetRequiredService<PlanRunCloser>(),
            sp.GetRequiredService<ILoggerFactory>()));
        services.AddSingleton<IConversationRunner>(sp => sp.GetRequiredService<QueryStreamEngine>());
        services.AddSingleton<ICacheSafeParamsProvider>(sp => sp.GetRequiredService<QueryStreamEngine>());

        services.AddSingleton<InputQueue>();

        return services;
    }
}
