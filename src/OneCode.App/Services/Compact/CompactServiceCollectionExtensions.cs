using Microsoft.Extensions.DependencyInjection;

namespace OneCode.App.Services.Compact;

/// <summary>
/// Compact（对话压缩）领域 DI 注册——与压缩实现（<see cref="CompactService"/> /
/// <see cref="AutoCompactService"/>）同目录维护。由组合根 <see cref="OneCode.App.OneCodeApp"/> 显式调用。
/// </summary>
public static class CompactServiceCollectionExtensions
{
    public static IServiceCollection AddCompactServices(this IServiceCollection services)
    {
        services.AddSingleton<CompactSessionDependencies>();
        services.AddSingleton<CompactService>();
        services.AddSingleton<AutoCompactService>();
        services.AddSingleton<ReviewCacheService>();

        services.AddSingleton<CompactPromptBuilder>();
        services.AddSingleton<CompactApplier>();

        return services;
    }
}
