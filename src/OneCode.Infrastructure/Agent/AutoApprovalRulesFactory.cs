using Microsoft.Agents.AI;
using OneCode.Core.Permissions;

namespace OneCode.Infrastructure.Agent;

/// <summary>
/// Builds MAF <see cref="ToolApprovalAgent"/> <c>AutoApprovalRules</c> from the same
/// deterministic source as Layer-1 permission: <see cref="PermissionProfiles.Check"/>.
/// Auto-approve only when the check returns <see cref="PermissionDecision.Allow"/>.
/// Does not invoke <see cref="IPermissionChecker"/> / YOLO (those stay on the permission middleware path).
/// </summary>
public static class AutoApprovalRulesFactory
{
    /// <summary>
    /// Create rules for <paramref name="mode"/> using an empty path/rules context
    /// (working directory = <see cref="Environment.CurrentDirectory"/>). Prefer the
    /// overload that passes pipeline security fields when building a real agent.
    /// </summary>
    public static List<Func<ToolAutoApprovalRuleContext, ValueTask<bool>>> Create(
        PermissionMode mode)
        => Create(
            mode,
            Environment.CurrentDirectory,
            rulesBySource: null,
            additionalWorkingDirectories: null,
            sessionAllowlist: null);

    /// <summary>Create rules bound to the same security snapshot as Permission middleware.</summary>
    public static List<Func<ToolAutoApprovalRuleContext, ValueTask<bool>>> Create(
        PermissionMode mode,
        string workingDirectory,
        IReadOnlyDictionary<string, PermissionRuleGroup>? rulesBySource,
        IReadOnlyDictionary<string, AdditionalWorkingDirectory>? additionalWorkingDirectories,
        HashSet<string>? sessionAllowlist)
    {
        var permContext = new ToolPermissionContext
        {
            Mode = mode,
            WorkingDirectory = string.IsNullOrWhiteSpace(workingDirectory)
                ? Environment.CurrentDirectory
                : workingDirectory,
            RulesBySource = rulesBySource
                ?? new Dictionary<string, PermissionRuleGroup>(),
            AdditionalWorkingDirectories = additionalWorkingDirectories
                ?? new Dictionary<string, AdditionalWorkingDirectory>(),
            SessionAllowlist = sessionAllowlist
                ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase),
        };

        return
        [
            // MAF skills read-only tools (load_skill / read_skill_resource) auto-approve;
            // they are an AIContextProvider path, not OneCode PermissionChecker.
            AgentSkillsProvider.ReadOnlyToolsAutoApprovalRule,

            (ToolAutoApprovalRuleContext ctx) =>
            {
                var fc = ctx.FunctionCallContent;
                var input = fc.Arguments is not null
                    ? JsonSerializer.SerializeToElement(fc.Arguments)
                    : JsonSerializer.SerializeToElement(new { });

                var result = PermissionProfiles.Check(mode, fc.Name, input, permContext);
                return new ValueTask<bool>(result.Decision == PermissionDecision.Allow);
            }
        ];
    }
}

