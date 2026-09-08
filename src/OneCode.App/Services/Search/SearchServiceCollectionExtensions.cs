using Microsoft.Extensions.DependencyInjection;

namespace OneCode.App.Services.Search;

/// <summary>
/// Search（文本检索）领域 DI 注册——与检索实现（<see cref="TextSearchService"/>）
/// 同目录维护。由组合根 <see cref="OneCode.App.OneCodeApp"/> 显式调用。
/// </summary>
public static class SearchServiceCollectionExtensions
{
    public static IServiceCollection AddSearchServices(this IServiceCollection services)
    {
        services.AddSingleton<ITextSearchService, TextSearchService>();

        return services;
    }
}
