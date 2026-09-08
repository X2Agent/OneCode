using Microsoft.Extensions.DependencyInjection;
using OneCode.Automation;
using OneCode.Core.Permissions.Yolo;
using OneCode.Infrastructure.Permissions.Yolo;

namespace OneCode.App.Services.Permissions;

/// <summary>
/// Permission 领域 DI 注册——Yolo 规则存储/分类器（Core / Infrastructure）与权限检查
/// 在此组装。由组合根 <see cref="OneCode.App.OneCodeApp"/> 显式调用。
/// </summary>
public static class PermissionServiceCollectionExtensions
{
    public static IServiceCollection AddPermissionServices(this IServiceCollection services)
    {
        services.AddSingleton<YoloRuleStore>();
        services.AddSingleton<YoloRuleFileStore>();
        services.AddSingleton<IYoloRuleFileStore>(sp => sp.GetRequiredService<YoloRuleFileStore>());
        services.AddSingleton<YoloClassifier>();
        services.AddSingleton<IYoloClassifier>(sp => sp.GetRequiredService<YoloClassifier>());
        services.AddSingleton<IPermissionChecker, PermissionChecker>();
        services.AddYoloRuleStoreLoader();

        services.AddSingleton<PermissionModeProvider>();
        services.AddSingleton<IPermissionModeProvider>(sp => sp.GetRequiredService<PermissionModeProvider>());

        return services;
    }
}
