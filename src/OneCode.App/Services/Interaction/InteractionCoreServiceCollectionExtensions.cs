using Microsoft.Extensions.DependencyInjection;
using OneCode.Core.Goals;

namespace OneCode.App.Services.Interaction;

/// <summary>
/// 交互核心 DI 注册——无头 dispatcher 默认实现与三个 Core 端口的交互式实现。
/// 无接收者时端口实现各自降级（审批拒绝 / 提问空结果 / 预算暂停）；
/// 交互式前端在各自注册段经 <see cref="InteractionServiceCollectionExtensions.ReplaceInteractionDispatcher{TDispatcher}"/> 覆盖。
/// </summary>
public static class InteractionCoreServiceCollectionExtensions
{
    public static IServiceCollection AddInteractionCoreServices(this IServiceCollection services)
    {
        services.AddSingleton<IInteractionDispatcher, HeadlessInteractionDispatcher>();
        services.AddSingleton<IApprovalPromptSink, InteractionApprovalPromptSink>();
        services.AddSingleton<IUserQuestionService, InteractionUserQuestionService>();
        services.AddSingleton<IBudgetDecisionService, InteractionBudgetDecisionService>();

        return services;
    }
}
