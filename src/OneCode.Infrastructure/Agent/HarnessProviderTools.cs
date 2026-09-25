using Microsoft.Agents.AI;
using OneCode.Core.Tools;

namespace OneCode.Infrastructure.Agent;

/// <summary>
/// MAF 运行期注入工具（Harness 待办清单 / 会话工作记忆）的统一名单与审批策略。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么单独声明。</b>这批工具不经过产品工具目录：Harness 在 <c>TodoProvider</c> /
/// <c>FileMemoryProvider</c> 内部构造它们，等到 <see cref="AgentPipelineBuilder"/> 施加构建期审批标记
/// 之后才并入请求。名字与策略集中在这里，供两个消费者共用，避免两边名单漂移：
/// <list type="bullet">
///   <item><description><see cref="RegisterMetadata"/>：让「这些工具需要审批边界」成为元数据注册表里的
///   显式决策，而不是依赖「未注册名 ⇒ Destructive/Always」这一默认值侥幸成立。</description></item>
///   <item><description><see cref="AutoApprovalRule"/>：让它们保持静默——模型几乎每轮都会更新待办清单，
///   逐次弹窗会让审批能力不可用。</description></item>
/// </list>
/// </para>
/// <para>
/// <b>风险级别取值。</b>两类工具都登记为 <see cref="ToolRisk.Safe"/>：它们只读写 agent 自己的会话状态
/// （待办清单、项目内的 <c>.onecode/agent-file-memory</c> 目录），不触达用户工作区文件，因此既不是
/// <see cref="ToolRisk.ReadOnly"/>（那会把它们并入只读工具集合、参与路径校验），也不是
/// <see cref="ToolRisk.Destructive"/>。安全决策仍由权限检查器负责，这里只回答「是否需要有审批边界」。
/// </para>
/// <para>
/// <b>按名匹配的注意事项。</b>与 MAF <c>AgentSkillsProvider.ReadOnlyToolsAutoApprovalRule</c> 同款：
/// 规则按工具名匹配，将来若出现同名产品工具，它会被一并自动批准。产品目录当前没有同名工具，新增工具时
/// 需要避开这些名字。
/// </para>
/// </remarks>
public static class HarnessProviderTools
{
    /// <summary>Harness 待办清单工具名（MAF 未导出常量，按其 <c>TodoProvider</c> 声明列出）。</summary>
    public static readonly IReadOnlySet<string> TodoToolNames =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "todos_add",
            "todos_complete",
            "todos_remove",
            "todos_get_remaining",
            "todos_get_all",
        };

    /// <summary>Harness 会话工作记忆工具名（取自 MAF 公开常量）。</summary>
    public static readonly IReadOnlySet<string> FileMemoryToolNames =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            FileMemoryProvider.WriteToolName,
            FileMemoryProvider.ReadFileToolName,
            FileMemoryProvider.DeleteFileToolName,
            FileMemoryProvider.LsToolName,
            FileMemoryProvider.GrepToolName,
            FileMemoryProvider.ReplaceToolName,
            FileMemoryProvider.ReplaceLinesToolName,
        };

    /// <summary>两类工具名的并集，供按名匹配的自动批准规则使用。</summary>
    public static IReadOnlySet<string> Names { get; } = MergeAll();

    /// <summary>
    /// 把已挂载 provider 的工具登记进产品元数据注册表。
    /// </summary>
    /// <param name="registry">产品工具元数据注册表。</param>
    /// <param name="includeTodo">本次 run 是否挂载了 Harness 待办清单 provider。</param>
    /// <param name="includeFileMemory">本次 run 是否挂载了 Harness 工作记忆 provider。</param>
    public static void RegisterMetadata(
        ToolMetadataRegistry registry,
        bool includeTodo,
        bool includeFileMemory)
    {
        if (includeTodo)
        {
            foreach (var name in TodoToolNames)
                Register(registry, name);
        }

        if (includeFileMemory)
        {
            foreach (var name in FileMemoryToolNames)
                Register(registry, name);
        }
    }

    /// <summary>按工具名自动批准上述工具，使注入的工具进入审批边界后仍然静默。</summary>
    public static Func<ToolAutoApprovalRuleContext, ValueTask<bool>> AutoApprovalRule { get; } =
        ctx => new ValueTask<bool>(Names.Contains(ctx.FunctionCallContent.Name));

    /// <summary>
    /// 不可见 + Deferred：这些工具由 provider 直接注入，模型不需要（也不能）经 ToolSearch 选中它们，
    /// 本地模型选路与检索索引不应把它们计入。
    /// </summary>
    private static void Register(ToolMetadataRegistry registry, string name) =>
        registry.Register(new ToolMetadata
        {
            Name = name,
            Risk = ToolRisk.Safe,
            ApprovalMode = ToolPolicyDefaults.ForRisk(ToolRisk.Safe),
            IsVisible = false,
            LoadPolicy = ToolLoadPolicy.Deferred,
        });

    private static IReadOnlySet<string> MergeAll()
    {
        var names = new HashSet<string>(TodoToolNames, StringComparer.OrdinalIgnoreCase);
        names.UnionWith(FileMemoryToolNames);
        return names;
    }
}
