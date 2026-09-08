using Microsoft.Extensions.DependencyInjection;
using OneCode.Core.Tokens;
using OneCode.Infrastructure.Api;

namespace OneCode.App.Services.Observability;

/// <summary>
/// Observability（Token 计量）领域 DI 注册——与计量实现（<see cref="TokenUsageTracker"/> /
/// <see cref="TokenBreakdownEstimator"/>）同目录维护。由组合根 <see cref="OneCode.App.OneCodeApp"/> 显式调用。
/// </summary>
public static class ObservabilityServiceCollectionExtensions
{
    public static IServiceCollection AddTokenObservabilityServices(this IServiceCollection services)
    {
        services.AddSingleton<TokenLedger>();
        services.AddSingleton<ITokenLedger>(sp => sp.GetRequiredService<TokenLedger>());

        services.AddSingleton<TokenUsageTracker>();
        services.AddSingleton<ITokenUsageTracker>(sp => sp.GetRequiredService<TokenUsageTracker>());
        services.AddSingleton<TokenBreakdownEstimator>();
        services.AddSingleton<ITokenBreakdownEstimator>(sp => sp.GetRequiredService<TokenBreakdownEstimator>());

        return services;
    }
}
