namespace OneCode.Infrastructure.Agent;

/// <summary>
/// Agent pipeline assembly profile — controls middleware trimming, tool filtering, and approval flow.
/// <see cref="Full"/> is the authoritative baseline (Main agent); other profiles are intentional subsets.
/// </summary>
public enum PipelineProfile
{
    /// <summary>
    /// Main agent — full middleware stack, MAF ToolApprovalAgent, 3-strike task recovery,
    /// verification and behavior contracts enabled per permission profile.
    /// </summary>
    Full,

    /// <summary>
    /// General-purpose forked sub-agent — inherits parent permission mode and security context;
    /// omits Main-only orchestration hooks. Verification follows permission profile.
    /// </summary>
    Worker,

    /// <summary>
    /// Team orchestration member — fixed <see cref="Core.Permissions.PermissionMode.Team"/>,
    /// event-driven ApprovalBroker, no post-edit verification.
    /// </summary>
    TeamMember,

    /// <summary>
    /// Read-only research sub-agent — Explore tool whitelist, no verification or edit contracts.
    /// </summary>
    Explore,

    /// <summary>
    /// Read-only planning sub-agent — same middleware/tool constraints as Explore;
    /// role instruction differs at the caller layer.
    /// </summary>
    Plan,
}

/// <summary>
/// Profile-driven middleware and tool-filter defaults applied by <see cref="AgentPipelineOptionsFactory"/>.
/// </summary>
public sealed record PipelineProfileBehavior(
    bool EnableStateMachine,
    bool EnableTaskRecovery,
    bool EnableBehaviorContracts,
    bool EnableToolApproval,
    IReadOnlyList<string>? ReadOnlyToolWhitelist = null)
{
    /// <summary>Read-only sub-agent tool whitelist (matches ToolCatalog registration names).</summary>
    public static IReadOnlyList<string> ReadOnlyAgentTools { get; } =
    [
        "Read", "Grep", "Glob", "LS", "WebFetch", "WebSearch",
        "ToolSearch", "FindReferences", "SymbolSearch",
    ];

    public static PipelineProfileBehavior For(PipelineProfile profile) => profile switch
    {
        PipelineProfile.Full => new(true, true, true, true),
        PipelineProfile.Worker => new(true, false, true, true),
        PipelineProfile.TeamMember => new(true, false, true, false),
        PipelineProfile.Explore or PipelineProfile.Plan => new(true, false, false, true, ReadOnlyAgentTools),
        _ => throw new ArgumentOutOfRangeException(nameof(profile), profile, null),
    };

    /// <summary>Maps forked agent type strings to pipeline profiles.</summary>
    public static PipelineProfile FromAgentType(string? agentType) => agentType switch
    {
        "Explore" => PipelineProfile.Explore,
        "Plan" => PipelineProfile.Plan,
        _ => PipelineProfile.Worker,
    };

    /// <summary>System role instruction for read-only sub-agent types; null for general-purpose agents.</summary>
    public static string? GetRoleInstruction(PipelineProfile profile) => profile switch
    {
        PipelineProfile.Explore =>
            "You are an Explore sub-agent performing read-only research. You cannot modify files, " +
            "run shell commands, or make any changes. Report findings with exact file paths and line numbers.",
        PipelineProfile.Plan =>
            "You are a Plan sub-agent designing implementation approaches. You cannot modify files or " +
            "run shell commands. Analyze the relevant code, then output a concrete step-by-step plan " +
            "with file paths and key decisions.",
        _ => null,
    };
}
