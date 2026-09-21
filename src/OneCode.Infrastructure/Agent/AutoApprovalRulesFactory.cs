using Microsoft.Agents.AI;
using OneCode.Core.Permissions;

namespace OneCode.Infrastructure.Agent;

/// <summary>
/// Builds MAF <see cref="ToolApprovalAgent"/> <c>AutoApprovalRules</c> from the product permission policy.
/// </summary>
/// <remarks>
/// <para>
/// <b>One policy source.</b> When an <see cref="IPermissionChecker"/> is supplied the rule delegates to it,
/// so the auto-approval decision and the execution-time permission decision come from the same code path.
/// Computing them independently (the checker for execution, <see cref="PermissionProfiles.Check"/> for
/// auto-approval) let the two disagree: the checker's Auto-mode YOLO rules could allow a call that the
/// auto-approval rule then failed to recognize, or vice versa.
/// </para>
/// <para>
/// The <see cref="PermissionProfiles.Check"/> overload remains for callers that have no checker
/// (non-interactive paths and tests). It is deterministic and shares the same profile definitions,
/// but it is the fallback, not a second authority.
/// </para>
/// <para>
/// <b>This is not a three-state authorization.</b> The callback is a boolean, so only
/// <see cref="PermissionDecision.Allow"/> auto-approves. <see cref="PermissionDecision.Deny"/> and
/// <see cref="PermissionDecision.Ask"/> both return false and leave the call to the human prompt;
/// a Deny never reaches that prompt because the execution-time middleware rejects it first.
/// </para>
/// </remarks>
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
            additionalWorkingDirectories: null);

    /// <summary>Create rules bound to the same security snapshot as Permission middleware.</summary>
    public static List<Func<ToolAutoApprovalRuleContext, ValueTask<bool>>> Create(
        PermissionMode mode,
        string workingDirectory,
        IReadOnlyDictionary<string, PermissionRuleGroup>? rulesBySource,
        IReadOnlyDictionary<string, AdditionalWorkingDirectory>? additionalWorkingDirectories,
        IPermissionChecker? permissionChecker = null)
    {
        var permContext = CreateContext(
            mode, workingDirectory, rulesBySource, additionalWorkingDirectories);

        return
        [
            // MAF skills read-only tools (load_skill / read_skill_resource) auto-approve;
            // they are an AIContextProvider path, not OneCode PermissionChecker.
            AgentSkillsProvider.ReadOnlyToolsAutoApprovalRule,

            permissionChecker is null
                ? CreateFromProfiles(mode, permContext)
                : CreateFromChecker(permissionChecker, permContext),
        ];
    }

    private static ToolPermissionContext CreateContext(
        PermissionMode mode,
        string workingDirectory,
        IReadOnlyDictionary<string, PermissionRuleGroup>? rulesBySource,
        IReadOnlyDictionary<string, AdditionalWorkingDirectory>? additionalWorkingDirectories) => new()
        {
            Mode = mode,
            WorkingDirectory = string.IsNullOrWhiteSpace(workingDirectory)
                ? Environment.CurrentDirectory
                : workingDirectory,
            RulesBySource = rulesBySource
                ?? new Dictionary<string, PermissionRuleGroup>(),
            AdditionalWorkingDirectories = additionalWorkingDirectories
                ?? new Dictionary<string, AdditionalWorkingDirectory>(),
        };

    /// <summary>
    /// Preferred path: the same checker the execution-time middleware uses, including Auto-mode
    /// YOLO rules and file-write shortcuts.
    /// </summary>
    private static Func<ToolAutoApprovalRuleContext, ValueTask<bool>> CreateFromChecker(
        IPermissionChecker permissionChecker,
        ToolPermissionContext permContext) =>
        ctx => CheckAsync(permissionChecker, ctx, permContext);

    private static async ValueTask<bool> CheckAsync(
        IPermissionChecker permissionChecker,
        ToolAutoApprovalRuleContext ctx,
        ToolPermissionContext permContext)
    {
        // No cancellation token is available at this layer: the rule runs inside the framework's
        // approval evaluation, which has no per-call token to hand down.
        var result = await permissionChecker
            .CheckAsync(ctx.FunctionCallContent.Name, SerializeInput(ctx), permContext, CancellationToken.None)
            .ConfigureAwait(false);

        return result.Decision == PermissionDecision.Allow;
    }

    /// <summary>
    /// Fallback for callers without a checker. Deterministic (no YOLO), so it is the conservative
    /// subset of the checker path.
    /// </summary>
    private static Func<ToolAutoApprovalRuleContext, ValueTask<bool>> CreateFromProfiles(
        PermissionMode mode,
        ToolPermissionContext permContext) =>
        ctx => new ValueTask<bool>(
            PermissionProfiles.Check(mode, ctx.FunctionCallContent.Name, SerializeInput(ctx), permContext)
                .Decision == PermissionDecision.Allow);

    private static JsonElement SerializeInput(ToolAutoApprovalRuleContext ctx) =>
        ctx.FunctionCallContent.Arguments is not null
            ? JsonSerializer.SerializeToElement(ctx.FunctionCallContent.Arguments)
            : JsonSerializer.SerializeToElement(new { });
}

