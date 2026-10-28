using Microsoft.Extensions.DependencyInjection;
using OneCode.Core.Tokens;
using OneCode.Infrastructure;
using OneCode.Infrastructure.Api;
using OneCode.Infrastructure.Persistence;

namespace OneCode.App.Services.Observability;

/// <summary>
/// Observability（Token 计量）领域 DI 注册——与计量实现（<see cref="TokenUsageTracker"/> /
/// <see cref="TokenBreakdownEstimator"/>）同目录维护。由组合根 <see cref="OneCode.App.OneCodeApp"/> 显式调用。
/// </summary>
public static class ObservabilityServiceCollectionExtensions
{
    public static IServiceCollection AddTokenObservabilityServices(this IServiceCollection services)
    {
        // Token 账本落盘水合：预算熔断跨重启一致（详见 JsonUsageStateStore 类注释）。
        services.AddSingleton(sp => new JsonUsageStateStore(
            PathsHelper.UserHome,
            sp.GetRequiredService<ILogger<JsonUsageStateStore>>()));
        services.AddSingleton<TokenLedger>(sp =>
            new TokenLedger(sp.GetRequiredService<JsonUsageStateStore>()));
        services.AddSingleton<ITokenLedger>(sp => sp.GetRequiredService<TokenLedger>());

        services.AddSingleton<TokenUsageTracker>();
        services.AddSingleton<ITokenUsageTracker>(sp => sp.GetRequiredService<TokenUsageTracker>());
        services.AddSingleton<TokenBreakdownEstimator>();
        services.AddSingleton<ITokenBreakdownEstimator>(sp => sp.GetRequiredService<TokenBreakdownEstimator>());

        return services;
    }
}
