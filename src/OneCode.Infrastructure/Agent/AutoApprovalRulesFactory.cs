using Microsoft.Agents.AI;
using OneCode.Core.Permissions;
using OneCode.Core.Tools;

namespace OneCode.Infrastructure.Agent;

/// <summary>
/// 基于 <see cref="PermissionProfile"/> 生成 MAF ToolApprovalAgent 的 AutoApprovalRules。
/// 逻辑搬自 MainAgentRunner.Approval.cs 的 CreateAutoApprovalRules，
/// 使 Worker/Team 路径也能获得 Profile 驱动的自动审批。
/// </summary>
public static class AutoApprovalRulesFactory
{
    /// <summary>
    /// 创建基于 Profile 的自动审批规则列表。
    /// 不含 AgentSkillsProvider.ReadOnlyToolsAutoApprovalRule（App 层规则，由调用方合并）。
    /// </summary>
    public static List<Func<ToolAutoApprovalRuleContext, ValueTask<bool>>> Create(
        PermissionProfile profile)
    {
        return
        [
            // 只读工具始终自动放行
            (ToolAutoApprovalRuleContext ctx) => new ValueTask<bool>(
                ToolNames.ReadOnlyTools.Contains(ctx.FunctionCallContent.Name)),

            // Profile 驱动的自动审批
            (ToolAutoApprovalRuleContext ctx) =>
            {
                var fc = ctx.FunctionCallContent;
                if (profile.AutoApproveAllTools)
                    return new ValueTask<bool>(true);

                if (profile.DenyAllNonReadOnly)
                {
                    if (fc.Name is "Bash" or "PowerShell" && profile.AutoApproveReadOnlyShell)
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

                if (fc.Name is "Bash" or "PowerShell" && profile.AutoApproveReadOnlyShell)
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
