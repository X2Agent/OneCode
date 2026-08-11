using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace OneCode.Core.Tools;

/// <summary>
/// 工具注册扩展方法——一次调用同时完成 DI 注册 + Catalog 元数据注册。
/// 消除原 <c>ServiceCollectionExtensions.Tools.cs</c>（DI 注册）和
/// <c>ToolCatalog.BuildStaticTools</c>（Catalog 注册）两处维护同一工具列表的问题。
/// </summary>
public static class ToolServiceCollectionExtensions
{
    /// <summary>
    /// 注册工具 POCO 到 DI（Singleton，TryAdd 避免重复）并记录 Catalog 元数据。
    /// ToolCatalog 在构建时通过反射调用 <c>MethodName</c> 创建 <c>AIFunction</c>。
    /// </summary>
    public static IServiceCollection AddTool<T>(
        this IServiceCollection services,
        string name,
        string methodName,
        ToolRisk risk,
        IReadOnlyList<string>? aliases = null,
        bool concurrency = true,
        bool visible = true,
        string? searchHint = null,
        ToolApprovalMode? approvalMode = null,
        ToolLoadPolicy loadPolicy = ToolLoadPolicy.Always,
        IReadOnlyList<string>? keywords = null,
        ToolCategory category = ToolCategory.None) where T : class
    {
        services.TryAddSingleton<T>();
        services.AddSingleton(new ToolRegistration(
            name, risk,
            ServiceType: typeof(T), MethodName: methodName, IsStatic: false,
            Aliases: aliases, Concurrency: concurrency, Visible: visible, SearchHint: searchHint,
            ApprovalMode: approvalMode ?? ToolPolicyDefaults.ForRisk(risk),
            InstanceFactory: static sp => sp.GetService(typeof(T)),
            LoadPolicy: loadPolicy, Keywords: keywords, Category: category));
        return services;
    }

    /// <summary>
    /// 注册静态方法工具（不需要 DI 实例）。
    /// </summary>
    public static IServiceCollection AddToolStatic(
        this IServiceCollection services,
        string name,
        Type type,
        string methodName,
        ToolRisk risk,
        IReadOnlyList<string>? aliases = null,
        bool concurrency = true,
        bool visible = true,
        string? searchHint = null,
        ToolApprovalMode? approvalMode = null,
        ToolLoadPolicy loadPolicy = ToolLoadPolicy.Always,
        IReadOnlyList<string>? keywords = null,
        ToolCategory category = ToolCategory.None)
    {
        services.AddSingleton(new ToolRegistration(
            name, risk,
            ServiceType: type, MethodName: methodName, IsStatic: true,
            Aliases: aliases, Concurrency: concurrency, Visible: visible, SearchHint: searchHint,
            ApprovalMode: approvalMode ?? ToolPolicyDefaults.ForRisk(risk),
            LoadPolicy: loadPolicy, Keywords: keywords, Category: category));
        return services;
    }
}
