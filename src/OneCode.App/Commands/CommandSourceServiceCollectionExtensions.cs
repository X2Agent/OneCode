using Microsoft.Extensions.DependencyInjection;
using OneCode.Infrastructure.Keybindings;

namespace OneCode.App.Commands;

/// <summary>
/// 命令面动态命令源 DI 注册——与命令源实现（<see cref="SkillCommandSource"/> /
/// <see cref="McpCommandSource"/>）同目录维护。由组合根 <see cref="OneCode.App.OneCodeApp"/> 显式调用。
/// </summary>
public static class CommandSourceServiceCollectionExtensions
{
    public static IServiceCollection AddCommandSourceServices(this IServiceCollection services)
    {
        services.AddSingleton<KeybindingLoader>();
        // IDynamicCommandSource 为 IEnumerable 注入：注册顺序即命令面枚举顺序。
        services.AddSingleton<IDynamicCommandSource, SkillCommandSource>();
        services.AddSingleton<IDynamicCommandSource, McpCommandSource>();

        return services;
    }
}
