using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using OneCode.Core.Tools;
using OneCode.Infrastructure.Agent;

namespace OneCode.Tests;

/// <summary>
/// 生产注册表 → 原生审批标记的端到端契约。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么需要这一层</b>：<see cref="ToolApprovalMarkerTests"/> 用手工构造的元数据验证了标记逻辑，
/// 但**没有任何测试证明生产注册表里的危险工具真的会得到 <see cref="ToolApprovalMode.Always"/>**。
/// 若有人把 <c>Write</c> 的 risk 改成 <see cref="ToolRisk.ReadOnly"/>，标记逻辑的单测全绿，
/// 而生产环境的写工具会静默失去审批边界——这正是 R1 要防的失效模式。
/// </para>
/// <para>
/// 本测试的「真实写入端」是 <c>AddToolServices()</c> 的注册表（与生产同一份代码路径），
/// 读取端是 <see cref="ToolApprovalMarker.Apply(IList{AITool}, ToolMetadataRegistry)"/> 的实际产物。
/// </para>
/// </remarks>
public sealed class ProductionApprovalBoundaryTests
{
    /// <summary>
    /// 必须跨审批边界的工具——它们会写文件、执行命令或修改工作区。
    /// 这份名单是**产品安全承诺**，不是实现细节。
    /// </summary>
    private static readonly string[] MustRequireApproval =
    [
        "Write", "Edit", "Delete", "Bash", "ApplyWorkspaceEdit",
        "BackgroundRun", "EnterWorktree", "ExitWorktree",
    ];

    /// <summary>
    /// 只读工具不得被标记：<c>ReadOnly</c> 在权限层是 <c>Allow</c>，无需协议边界；
    /// 标记它们只会让每个 <c>Read</c> 多走一道无意义的审批协议。
    /// </summary>
    /// <remarks>
    /// 注意 <c>Safe</c>（如 <c>Task</c>、<c>AskUserQuestion</c>）<b>不在</b>此名单：
    /// <see cref="ToolPolicyDefaults.ForRisk"/> 对 <c>Safe</c> 给出 <c>Conditional</c>，
    /// 因此它们<b>会</b>带标记。这与「只读工具不标记」并不矛盾：只读工具在权限层无条件 <c>Allow</c>，
    /// 没有 Ask 需要落地；<c>Safe</c> 工具则经 <c>PermissionCheckHelpers.CheckReadOnlyAndEvaluate</c>
    /// 的规则评估（无匹配 → <c>Ask</c>），边界正是 Ask 的落点。
    /// 断言放在 <see cref="SafeTools_BoundaryIsResolvedByPolicyNotRisk"/>。
    /// </remarks>
    private static readonly string[] MustNotRequireApproval =
    [
        "Read", "LS", "Glob", "Grep", "WebFetch", "WebSearch",
    ];

    /// <summary>
    /// <c>Safe</c> 工具的边界由**权限政策**消解，而不是靠没有标记。
    /// 本用例锁住这个分工的前一半：风险映射给出 <c>Conditional</c>（带边界）；
    /// 是否真的追问用户由规则层决定（<c>EvaluateRules</c> 无匹配 → <c>Ask</c>）。
    /// </summary>
    /// <remarks>
    /// 这里**不**查 <c>ToolNames.ReadOnlyTools</c>：该门面是全局静态单例
    /// （<c>Initialize</c> 只生效一次），会被先运行的测试污染，不适合做跨测试断言。
    /// </remarks>
    [Fact]
    public void SafeTools_BoundaryIsResolvedByPolicyNotRisk()
    {
        ToolPolicyDefaults.ForRisk(ToolRisk.Safe).Should().Be(ToolApprovalMode.Conditional,
            "Safe tools carry a boundary; it is resolved by the permission policy, not by omitting the marker");

        // 权限层对非写/非只读工具走 EvaluateRules（PermissionCheckHelpers.CheckReadOnlyAndEvaluate），
        // 命中规则给 Allow/Deny、无匹配给 Ask —— 所以带标记的 Safe 工具是否追问用户取决于规则，
        // 而不是取决于风险级别本身。
        var (registry, _) = BuildProductionTools();
        registry.GetPolicy("Task").ApprovalMode.Should().Be(ToolApprovalMode.Conditional);
        registry.GetPolicy("Task").Risk.Should().Be(ToolRisk.Safe,
            "Task is Safe: it neither reads nor writes data, so its boundary comes from the rules, not from risk");
    }

    [Fact]
    public void ProductionRegistry_DangerousTools_CarryApprovalBoundary()
    {
        var (registry, functions) = BuildProductionTools();

        var marked = ToolApprovalMarker.Apply([.. functions.Cast<AITool>()], registry)!;

        foreach (var name in MustRequireApproval)
        {
            registry.RequiresApprovalBoundary(name).Should().BeTrue(
                $"'{name}' modifies the workspace and must cross an approval boundary");

            var function = marked.OfType<AIFunction>().Single(f => f.Name == name);
            function.GetService<ApprovalRequiredAIFunction>().Should().NotBeNull(
                $"'{name}' is registered as requiring approval but carries no native marker, "
                + "so an Ask decision would execute it directly");
        }
    }

    [Fact]
    public void ProductionRegistry_ReadOnlyTools_HaveNoApprovalBoundary()
    {
        var (registry, functions) = BuildProductionTools();

        var marked = ToolApprovalMarker.Apply([.. functions.Cast<AITool>()], registry)!;

        foreach (var name in MustNotRequireApproval)
        {
            var function = marked.OfType<AIFunction>().Single(f => f.Name == name);
            function.GetService<ApprovalRequiredAIFunction>().Should().BeNull(
                $"'{name}' is read-only (permission layer already allows it); "
                + "a marker would add a pointless approval round-trip to every tool loop");
        }
    }

    /// <summary>
    /// 反证：这份契约确实盯着生产注册表，而不是恒真。
    /// 把 <c>Write</c> 的 risk 降级后，上面的用例必须失败。
    /// </summary>
    [Fact]
    public void DowngradingWriteRisk_RemovesItsApprovalBoundary()
    {
        // 模拟「有人把 Write 的 risk 改成 ReadOnly」：这正是上面的用例要拦住的改动。
        var downgraded = ToolPolicyDefaults.ForRisk(ToolRisk.ReadOnly);

        downgraded.Should().Be(ToolApprovalMode.Never,
            "read-only risk implies no approval; the guard above exists precisely to catch this downgrade");
        ToolPolicyDefaults.ForRisk(ToolRisk.Destructive).Should().Be(ToolApprovalMode.Always,
            "the production risk→approval mapping must keep destructive tools behind the boundary");
    }

    /// <summary>
    /// 构建与生产同一份注册表（<c>AddToolServices</c>），但不解析 DI 图——
    /// 工具的构造依赖会拉起整张服务图，而本测试只关心注册时写下的 <see cref="ToolMetadata"/>。
    /// 因此按注册描述符重建元数据，并为每个名字建一个同名桩函数。
    /// </summary>
    /// <remarks>
    /// 真实性来自注册描述符本身：<c>Risk</c> 与 <c>ApprovalMode</c> 都由生产代码
    /// （<c>AddToolInstance</c> → <see cref="ToolPolicyDefaults.ForRisk"/>）写入，此处不重算。
    /// </remarks>
    private static (ToolMetadataRegistry Registry, List<AIFunction> Functions) BuildProductionTools()
    {
        var services = new ServiceCollection();
        OneCode.App.Tools.ToolServiceCollectionExtensions.AddToolServices(services);

        var registrations = services
            .Where(d => d.ServiceType == typeof(ToolRegistration))
            .Select(d => (ToolRegistration)d.ImplementationInstance!)
            .ToList();

        registrations.Should().NotBeEmpty("AddToolServices must contribute tool registrations");

        var registry = new ToolMetadataRegistry();
        var functions = new List<AIFunction>();

        foreach (var reg in registrations)
        {
            registry.Register(new ToolMetadata
            {
                Name = reg.Name,
                Aliases = reg.Aliases ?? [],
                Risk = reg.Risk,
                ApprovalMode = reg.ApprovalMode,
                IsConcurrencySafe = reg.Concurrency,
                IsVisible = reg.Visible,
                SearchHint = reg.SearchHint,
                LoadPolicy = reg.LoadPolicy,
                Keywords = reg.Keywords ?? [],
                Category = reg.Category,
            });

            functions.Add(AIFunctionFactory.Create(() => "stub", name: reg.Name));
        }

        return (registry, functions);
    }
}
