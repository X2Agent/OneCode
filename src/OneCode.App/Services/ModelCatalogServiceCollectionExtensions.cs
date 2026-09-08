using Microsoft.Extensions.DependencyInjection;
using OneCode.Core.Models;
using OneCode.Automation;
using OneCode.Infrastructure.Model;

namespace OneCode.App.Services;

/// <summary>
/// 模型目录领域 DI 注册——与模型实现（<see cref="ModelManager"/>）同层维护；
/// <see cref="ModelsDevClient"/>（Infrastructure）在此一并组装。
/// 由组合根 <see cref="OneCode.App.OneCodeApp"/> 显式调用。
/// </summary>
public static class ModelCatalogServiceCollectionExtensions
{
    public static IServiceCollection AddModelCatalogServices(this IServiceCollection services)
    {
        services.AddSingleton<ModelCatalogStore>();
        services.AddSingleton<IModelCatalog>(sp => sp.GetRequiredService<ModelCatalogStore>());

        services.AddSingleton<ModelManager>();
        services.AddSingleton<IModelManager>(sp => sp.GetRequiredService<ModelManager>());

        services.AddSingleton<ModelsDevClient>();
        services.AddSingleton<IModelCatalogCache>(sp =>
        {
            var client = sp.GetRequiredService<ModelsDevClient>();
            var catalogStore = sp.GetRequiredService<ModelCatalogStore>();
            var logger = sp.GetRequiredService<ILogger<ModelCatalogCacheService>>();
            var cache = new ModelCatalogCacheService(client, catalogStore, logger);
            cache.TryLoadFromCache();
            return cache;
        });
        services.AddModelCatalogRefresh();

        return services;
    }
}
