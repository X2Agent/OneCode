using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace OneCode.App.Services.Interaction;

/// <summary>
/// 交互前端的端口注册：前端只提供 <see cref="IInteractionDispatcher"/> 实现，
/// 三个 Core 端口的交互式实现由 <c>AddInteractionCoreServices</c> 统一注册为默认，
/// 本方法仅把无头 dispatcher 替换为前端实现。
/// </summary>
public static class InteractionServiceCollectionExtensions
{
    /// <summary>用前端实现替换默认的 <see cref="IInteractionDispatcher"/>；<typeparamref name="TDispatcher"/> 必须已注册为具体服务。</summary>
    public static IServiceCollection ReplaceInteractionDispatcher<TDispatcher>(this IServiceCollection services)
        where TDispatcher : class, IInteractionDispatcher
    {
        services.Replace(ServiceDescriptor.Singleton<IInteractionDispatcher>(sp => sp.GetRequiredService<TDispatcher>()));
        return services;
    }
}