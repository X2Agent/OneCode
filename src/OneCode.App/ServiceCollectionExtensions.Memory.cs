using Microsoft.Extensions.DependencyInjection;
using OneCode.App.Services;
using OneCode.App.Services.Compact;
using OneCode.App.Services.Memory;
using OneCode.Core.Memory;

namespace OneCode.App;

public static partial class ServiceCollectionExtensions
{
    public static IServiceCollection RegisterMemoryServices(this IServiceCollection services)
    {
        services.AddSingleton<IMemoryEntryStore>(sp => new MemoryEntryStore(
            sp.GetRequiredService<IWorkingDirectoryAccessor>(),
            sp.GetRequiredService<ILogger<MemoryEntryStore>>()));

        services.AddSingleton<MemoryService>();
        services.AddSingleton<IMemoryService>(sp => sp.GetRequiredService<MemoryService>());
        services.AddSingleton<SessionMemoryService>();
        services.AddSingleton<ISessionMemoryService>(sp => sp.GetRequiredService<SessionMemoryService>());

        services.AddSingleton<CompactSessionDependencies>();
        services.AddSingleton<CompactService>();
        services.AddSingleton<AutoCompactService>();
        services.AddSingleton<ReviewCacheService>();

        services.AddSingleton<CompactPromptBuilder>();
        services.AddSingleton<CompactApplier>();

        return services;
    }
}
