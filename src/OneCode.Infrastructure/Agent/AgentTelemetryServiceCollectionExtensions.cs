using Microsoft.Extensions.DependencyInjection;

namespace OneCode.Infrastructure.Agent;

public static class AgentTelemetryServiceCollectionExtensions
{
    /// <summary>
    /// 注册 <see cref="AgentTelemetryLogForwarder"/>（singleton，容器停止时随容器释放监听器）。
    /// 注册本身不挂监听——组合根需在宿主构建后解析一次以尽早订阅，避免漏掉启动阶段的 span。
    /// </summary>
    public static IServiceCollection AddAgentTelemetryLogForwarding(this IServiceCollection services)
    {
        services.AddSingleton<AgentTelemetryLogForwarder>();
        return services;
    }
}
