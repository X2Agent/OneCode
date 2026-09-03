using Microsoft.Agents.AI;
using OneCode.Core.Permissions;
using OneCode.Core.Tools;

namespace OneCode.Infrastructure.Agent;

/// <summary>
/// 基于 <see cref="PermissionProfile"/> 生成 MAF ToolApprovalAgent 的 AutoApprovalRules（单一来源）。
/// </summary>
public static class AutoApprovalRulesFactory
{
    /// <summary>创建基于 Profile 的自动审批规则列表。</summary>
    public static List<Func<ToolAutoApprovalRuleContext, ValueTask<bool>>> Create(
        PermissionProfile profile)
    {
        return
        [
            // MAF skills 只读工具（load_skill / read_skill_resource）自动放行；
            // 走 AIContextProvider，不经过 OneCode PermissionChecker。
            AgentSkillsProvider.ReadOnlyToolsAutoApprovalRule,

            // Profile 驱动的自动审批
            (ToolAutoApprovalRuleContext ctx) =>
            {
                var fc = ctx.FunctionCallContent;
                if (profile.AutoApproveAllTools)
                    return new ValueTask<bool>(true);

                if (profile.DenyAllNonReadOnly)
                {
                    if (fc.Name is "Bash" && profile.AutoApproveReadOnlyShell)
                    {
                        var input = fc.Arguments is not null
                            ? JsonSerializer.SerializeToElement(fc.Arguments)
                            : JsonSerializer.SerializeToElement(new { });
                        return new ValueTask<bool>(
                            PermissionCheckHelpers.IsReadOnlyShell(fc.Name, input));
                    }
                    return new ValueTask<bool>(false);
                }

                if (profile.AutoApproveFileWrites && ToolNames.FileWriteTools.Contains(fc.Name))
                    return new ValueTask<bool>(true);

                if (fc.Name is "Bash" && profile.AutoApproveReadOnlyShell)
                {
                    var input = fc.Arguments is not null
                            ? JsonSerializer.SerializeToElement(fc.Arguments)
                            : JsonSerializer.SerializeToElement(new { });
                    return new ValueTask<bool>(
                        PermissionCheckHelpers.IsReadOnlyShell(fc.Name, input));
                }

                return new ValueTask<bool>(false);
            }
        ];
    }

    /// <summary>
    /// 从 PermissionMode 获取 Profile 并生成规则。
    /// </summary>
    public static List<Func<ToolAutoApprovalRuleContext, ValueTask<bool>>> Create(
        PermissionMode mode)
    {
        var profile = PermissionProfiles.GetProfile(mode);
        return Create(profile);
    }
}
