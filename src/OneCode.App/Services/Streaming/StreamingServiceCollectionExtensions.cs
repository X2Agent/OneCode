using Microsoft.Extensions.DependencyInjection;
using OneCode.App.Services.BuildMode;

namespace OneCode.App.Services.Streaming;

/// <summary>
/// 流式查询 DI 注册——查询管道依赖、编排流合并、Build 回放与 QueryEvent 旁路。
/// </summary>
public static class StreamingServiceCollectionExtensions
{
    public static IServiceCollection AddStreamingServices(this IServiceCollection services)
    {
        services.AddSingleton<PromptConfigBuilder>();
        services.AddSingleton<ThinkingParamsResolver>();
        services.AddSingleton<OrchestrationStreamService>();
        services.AddSingleton<QueryOrchestrationDependencies>();
        services.AddSingleton<QueryRuntimeDependencies>();
        services.AddSingleton<IQueryStreamService, QueryStreamService>();
        services.AddSingleton<BuildRunReplayService>();
        services.AddSingleton<RunEventBridge>();

        return services;
    }
}
