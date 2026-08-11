using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace OneCode.App.Tools;

/// <summary>
/// App 层工具注册扩展——补充 <see cref="ToolServiceCollectionExtensions"/> 不支持的场景。
/// <see cref="AddToolInstance"/> 用于需要 <see cref="AIFunctionFactory"/> 创建 AIFunction 的特殊工具
/// （如 ToolSearchTool 需要运行时访问 ToolMetadataRegistry）。
/// </summary>
public static class ToolRegistrationExtensions
{
    /// <summary>
    /// 注册实例工具——AIFunction 由工厂在运行时创建。
    /// 用于无法通过 <c>AddTool&lt;T&gt;</c> 反射调用的特殊工具。
    /// </summary>
    public static IServiceCollection AddToolInstance(
        this IServiceCollection services,
        string name,
        Func<IServiceProvider, AIFunction> functionFactory,
        ToolRisk risk,
        IReadOnlyList<string>? aliases = null,
        bool concurrency = true,
        bool visible = true,
        string? searchHint = null,
        ToolApprovalMode? approvalMode = null,
        ToolLoadPolicy loadPolicy = ToolLoadPolicy.Always,
        IReadOnlyList<string>? keywords = null)
    {
        services.AddSingleton(new ToolRegistration(
            name, risk,
            FunctionFactory: functionFactory,
            Aliases: aliases, Concurrency: concurrency, Visible: visible, SearchHint: searchHint,
            ApprovalMode: approvalMode ?? ToolPolicyDefaults.ForRisk(risk),
            LoadPolicy: loadPolicy, Keywords: keywords));
        return services;
    }
}
