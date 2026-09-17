using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace OneCode.Core.Tools;

/// <summary>工具注册扩展，使用显式实例委托创建 AIFunction。</summary>
public static class ToolRegistrationExtensions
{
    /// <summary>
    /// 注册工具实例并保存显式 AIFunction 工厂，避免运行时方法反射。
    /// 这是工具注册的<b>唯一入口</b>：同时完成 DI 注册（<c>TryAddSingleton&lt;T&gt;</c>）与
    /// <see cref="ToolRegistration"/> 元数据登记。
    /// </summary>
    /// <remarks>
    /// <c>approvalMode</c> 省略时按 <c>risk</c> 推导（见 <see cref="ToolPolicyDefaults.ForRisk"/>），
    /// 且<b>只在此处推导一次</b>——<see cref="ToolRegistration.ApprovalMode"/> 为非空字段，消费方直接使用。
    /// </remarks>
    public static IServiceCollection AddToolInstance<T>(
        this IServiceCollection services,
        string name,
        Func<T, AIFunction> functionFactory,
        ToolRisk risk,
        IReadOnlyList<string>? aliases = null,
        bool concurrency = true,
        bool visible = true,
        string? searchHint = null,
        ToolApprovalMode? approvalMode = null,
        ToolLoadPolicy loadPolicy = ToolLoadPolicy.Always,
        IReadOnlyList<string>? keywords = null,
        ToolCategory category = ToolCategory.None)
        where T : class
    {
        services.TryAddSingleton<T>();

        services.AddSingleton(new ToolRegistration(
            name,
            risk,
            FunctionFactory: sp => functionFactory(sp.GetRequiredService<T>()),
            // 默认值在此计算一次，ToolCatalog 直接消费，避免同一语义在两处推导。
            ApprovalMode: approvalMode ?? ToolPolicyDefaults.ForRisk(risk),
            Aliases: aliases,
            Concurrency: concurrency,
            Visible: visible,
            SearchHint: searchHint,
            LoadPolicy: loadPolicy,
            Keywords: keywords,
            Category: category));
        return services;
    }
}