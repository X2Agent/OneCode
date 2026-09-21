using Microsoft.Extensions.AI;
using OneCode.Core.Tools;

namespace OneCode.Infrastructure.Agent;

/// <summary>
/// Marks product tools that require an approval boundary with MAF's
/// <see cref="ApprovalRequiredAIFunction"/> so the framework's approval protocol actually runs for them.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> The product permission layer decides Allow / Deny / Ask, but that decision
/// alone does not make the agent runtime ask anyone: a tool must carry
/// <see cref="ApprovalRequiredAIFunction"/> for <c>FunctionInvokingChatClient</c> to surface a
/// <see cref="ToolApprovalRequestContent"/> instead of invoking it. Without this marker an <c>Ask</c>
/// decision silently degrades into "execute anyway", because the permission middleware treats
/// <c>Ask</c> as a pass-through to the approval layer — which then has nothing to approve.
/// </para>
/// <para>
/// <b>Where it runs.</b> Only at pipeline build time, and only when the path actually has approval
/// machinery (<c>EnableToolApproval</c>). Marking tools on a path with no <c>ToolApprovalAgent</c> and
/// no interactive bridge would make the framework convert every function call in the batch into an
/// unresolvable approval request.
/// </para>
/// <para>
/// <b>Idempotent.</b> Tools that already carry the marker are left alone, so provider-supplied tools
/// (skills, file access, shell executors) are not double-wrapped. MAF's auto-approval rules match by
/// tool <i>name</i>, so double wrapping would not be detected by name alone — the marker lookup is the
/// only reliable check.
/// </para>
/// </remarks>
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
}
