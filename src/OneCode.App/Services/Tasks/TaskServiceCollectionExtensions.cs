using Microsoft.Extensions.DependencyInjection;
using OneCode.Core.Tasks;
using OneCode.Infrastructure.Tasks;

namespace OneCode.App.Services.Tasks;

/// <summary>
/// Task（后台任务清单）领域 DI 注册——实现位于 Core.Tasks / Infrastructure.Tasks，
/// App 侧注册点与其消费者（TaskTool / AgentTool / BuildTaskLinker 等）同层维护。
/// 由组合根 <see cref="OneCode.App.OneCodeApp"/> 显式调用。
/// </summary>
public static class TaskServiceCollectionExtensions
{
    public static IServiceCollection AddTaskServices(this IServiceCollection services)
    {
        services.AddSingleton<ITaskStore, JsonTaskStore>();
        services.AddSingleton<TaskService>();
        services.AddSingleton<ITaskService>(sp => sp.GetRequiredService<TaskService>());

        return services;
    }
}
