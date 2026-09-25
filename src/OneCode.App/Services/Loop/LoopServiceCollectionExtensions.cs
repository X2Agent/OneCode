using Microsoft.Extensions.DependencyInjection;

namespace OneCode.App.Services.Loop;

/// <summary>
/// <c>/loop</c> 运行时循环 DI 注册——与实现（<see cref="IterativeLoopService"/>）同目录维护。
/// </summary>
public static class LoopServiceCollectionExtensions
{
    public static IServiceCollection AddLoopServices(this IServiceCollection services)
    {
        services.AddSingleton<IIterativeLoopService, IterativeLoopService>();
        return services;
    }
}
