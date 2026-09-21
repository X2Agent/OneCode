using Microsoft.Extensions.DependencyInjection;
using OneCode.Infrastructure.Memory;
using OneCode.Core.Memory;

namespace OneCode.App.Services.Memory;

/// <summary>
/// Memory 领域 DI 注册——与记忆实现（<see cref="MemoryService"/>）
/// 同目录维护。由组合根 <see cref="OneCode.App.OneCodeApp"/> 显式调用。
/// </summary>
public static class MemoryServiceCollectionExtensions
{
    public static IServiceCollection AddMemoryServices(this IServiceCollection services)
    {
        services.AddSingleton<IMemoryEntryStore>(sp => new MemoryEntryStore(
            sp.GetRequiredService<IWorkingDirectoryAccessor>(),
            sp.GetRequiredService<ILogger<MemoryEntryStore>>()));

        services.AddSingleton<MemoryService>();
        services.AddSingleton<IMemoryService>(sp => sp.GetRequiredService<MemoryService>());

        return services;
    }
}
