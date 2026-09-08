using Microsoft.Extensions.DependencyInjection;
using OneCode.App.Services;
using OneCode.App.Services.Compact;

namespace OneCode.App.Query;

/// <summary>
/// Chat 查询领域 DI 注册——与查询实现（<see cref="ChatService"/>）同目录维护。
/// 由组合根 <see cref="OneCode.App.OneCodeApp"/> 显式调用。
/// </summary>
public static class ChatQueryServiceCollectionExtensions
{
    public static IServiceCollection AddChatQueryServices(this IServiceCollection services)
    {
        services.AddSingleton<ChatSessionDependencies>();
        services.AddSingleton<ChatObservabilityDependencies>();
        services.AddSingleton<IToolProtocolValidator, ToolProtocolValidator>();
        services.AddSingleton<ChatService>();
        services.AddSingleton<IConversationRunner>(sp => sp.GetRequiredService<ChatService>());
        services.AddSingleton<ICacheSafeParamsProvider>(
            sp => sp.GetRequiredService<ChatService>());

        services.AddSingleton<InputQueue>();

        return services;
    }
}
