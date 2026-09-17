using Microsoft.Extensions.AI;

namespace OneCode.Core.Tools;

/// <summary>
/// 工具注册信息——存储显式 AIFunction 工厂和 Catalog 元数据。
/// 由工具注册扩展方法创建，
/// 由 App 层 <c>ToolCatalog</c> 消费以构建 <see cref="AIFunction"/> 列表。
///
/// 设计目标：消除"DI 注册"和"Catalog 元数据注册"两处维护——
/// 每个工具只需调用一次显式注册方法，同时完成 DI 注册和元数据注册。
/// </summary>
/// <remarks>
/// <see cref="ApprovalMode"/> 为必填且非空：默认值由
/// <see cref="ToolPolicyDefaults.ForRisk"/> 在注册入口（<see cref="ToolRegistrationExtensions"/>）
/// 计算一次，消费方（<c>ToolCatalog</c>）直接使用，不重复推导。
/// </remarks>
public sealed record ToolRegistration(
    string Name,
    ToolRisk Risk,
    Func<IServiceProvider, AIFunction> FunctionFactory,
    ToolApprovalMode ApprovalMode,
    IReadOnlyList<string>? Aliases = null,
    bool Concurrency = true,
    bool Visible = true,
    string? SearchHint = null,
    ToolLoadPolicy LoadPolicy = ToolLoadPolicy.Always,
    IReadOnlyList<string>? Keywords = null,
    ToolCategory Category = ToolCategory.None);
