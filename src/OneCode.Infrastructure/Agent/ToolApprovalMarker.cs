using Microsoft.Extensions.AI;
using OneCode.Core.Tools;

namespace OneCode.Infrastructure.Agent;

/// <summary>
/// 为产品工具挂上 <see cref="ApprovalRequiredAIFunction"/>，让 MAF 真正发起审批请求。
/// 缺少标记时 <c>Ask</c> 决策会静默退化为直接执行。
/// 在管道构建时与每次请求时各执行一次（后者覆盖框架注入的工具），仅在具备审批机制的通路上生效；
/// 已带标记的工具跳过，因为按名称匹配无法识别重复包装。
/// </summary>
public static class ToolApprovalMarker
{
    /// <summary>
    /// Returns a copy of <paramref name="tools"/> in which every tool whose product metadata requires
    /// an approval boundary is wrapped in <see cref="ApprovalRequiredAIFunction"/>.
    /// </summary>
    /// <param name="tools">Tools to mark. Non-function tools pass through untouched.</param>
    /// <param name="metadata">Registry that owns the product <c>ApprovalMode</c> for each tool name.</param>
    /// <returns>
    /// A new list when anything was wrapped, otherwise the original list so the common
    /// no-change case allocates nothing.
    /// </returns>
    public static IList<AITool>? Apply(IList<AITool>? tools, ToolMetadataRegistry? metadata)
    {
        if (tools is null || tools.Count == 0 || metadata is null)
            return tools;

        List<AITool>? marked = null;
        for (var i = 0; i < tools.Count; i++)
        {
            var tool = tools[i];
            if (tool is not AIFunction function
                || function.GetService<ApprovalRequiredAIFunction>() is not null
                || !metadata.RequiresApprovalBoundary(function.Name))
            {
                // Lazily materialize so an all-clear list keeps the caller's instance.
                marked?.Add(tool);
                continue;
            }

            marked ??= [.. tools.Take(i)];
            marked.Add(new ApprovalRequiredAIFunction(function));
        }

        return marked ?? tools;
    }

    /// <summary>
    /// Returns <paramref name="tools"/> with approval-boundary markers applied, materializing the
    /// sequence first when the caller cannot supply a list.
    /// </summary>
    /// <param name="tools">Tools to mark. Non-function tools pass through untouched.</param>
    /// <param name="metadata">Registry that owns the product <c>ApprovalMode</c> for each tool name.</param>
    /// <returns>
    /// The same sequence when nothing was wrapped, otherwise a list containing the marked tools.
    /// </returns>
    /// <remarks>
    /// The per-request path sees the tool list as an <see cref="IEnumerable{T}"/> that may be a lazy
    /// framework sequence, while the build-time path assigns straight into <c>ChatOptions.Tools</c> and
    /// keeps the allocation-free list overload.
    /// </remarks>
    public static IEnumerable<AITool>? Apply(IEnumerable<AITool>? tools, ToolMetadataRegistry? metadata)
    {
        if (tools is null || metadata is null)
            return tools;

        return Apply(tools as IList<AITool> ?? tools.ToList(), metadata);
    }
}
