using Microsoft.Extensions.AI;

namespace OneCode.Core.Tools;

/// <summary>
/// 工具注册信息——同时存储 DI 注册所需的类型信息和 Catalog 元数据。
/// 由 <see cref="ToolServiceCollectionExtensions.AddTool{T}"/> 等方法创建，
/// 由 App 层 <c>ToolCatalog</c> 消费以构建 <see cref="AIFunction"/> 列表。
///
/// 设计目标：消除"DI 注册"和"Catalog 元数据注册"两处维护——
/// 每个工具只需调用一次 <c>AddTool&lt;T&gt;</c>，同时完成 DI 注册和元数据注册。
/// </summary>
public sealed record ToolRegistration(
    string Name,
    ToolRisk Risk,
    Type? ServiceType = null,
    string? MethodName = null,
    bool IsStatic = false,
    Func<IServiceProvider, AIFunction>? FunctionFactory = null,
    IReadOnlyList<string>? Aliases = null,
    bool Concurrency = true,
    bool Visible = true,
    string? SearchHint = null,
    ToolApprovalMode? ApprovalMode = null,
    Func<IServiceProvider, object?>? InstanceFactory = null,
    ToolLoadPolicy LoadPolicy = ToolLoadPolicy.Always,
    IReadOnlyList<string>? Keywords = null,
    ToolCategory Category = ToolCategory.None);
