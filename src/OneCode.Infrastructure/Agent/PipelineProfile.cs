using OneCode.Core.Agent;

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
    /// MAF approval protocol carried through the Team workflow approval bridge
    /// (see <c>TeamWorkflowRunner</c>), no post-edit verification.
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
/// Profile-driven capability set applied by <see cref="AgentPipelineOptionsFactory"/> and
/// <c>SharedContextProviderBuilder</c>.
/// </summary>
/// <remarks>
/// <para>
/// A profile is a <b>named set of <see cref="AgentCapability"/> values</b>. Reading a profile means
/// reading the set — there is no second place where "does Worker include LSP diagnostics?" is
/// answered. Previously the same decision lived in positional booleans here, in
/// <c>ApplyProfileDefaults</c> (provider flags), and again in <c>BuildCommon</c> (an <c>if</c> per
/// provider).
/// </para>
/// <para>
/// The <c>Enable*</c> members below are read-only projections kept so existing call sites stay
/// expressed as questions (<c>EnableToolApproval</c>) rather than set arithmetic. Add new
/// capabilities to the enum and to <see cref="AllCapabilities"/>; profiles subtract.
/// </para>
/// </remarks>
public sealed record PipelineProfileBehavior(IReadOnlySet<AgentCapability> Capabilities)
{
    /// <summary>Read-only sub-agent tool whitelist (matches ToolCatalog registration names).</summary>
    public static IReadOnlyList<string> ReadOnlyAgentTools { get; } =
    [
        "Read", "Grep", "Glob", "LS", "WebFetch", "WebSearch",
        "ToolSearch", "FindReferences", "SymbolSearch",
    ];

    /// <summary>Every capability any profile can hold. Profiles are expressed as subtractions from this.</summary>
    private static readonly IReadOnlySet<AgentCapability> AllCapabilities =
        new HashSet<AgentCapability>(Enum.GetValues<AgentCapability>());

    /// <summary>True when this profile includes the given capability.</summary>
    public bool Has(AgentCapability capability) => Capabilities.Contains(capability);

    // Middleware-axis projections (consumed by AgentPipelineOptionsFactory).

    /// <summary>Failure-tracking state machine; only Full keeps it (W2-A).</summary>
    public bool EnableStateMachine => Has(AgentCapability.StateMachine);

    /// <summary>Three-strike recovery guidance emitted by the state machine.</summary>
    public bool EnableTaskRecovery => Has(AgentCapability.TaskRecovery);

    /// <summary>Post-edit behavior contracts (mandatory file-edit validation).</summary>
    public bool EnableBehaviorContracts => Has(AgentCapability.BehaviorContracts);

    /// <summary>MAF tool-approval flow.</summary>
    public bool EnableToolApproval => Has(AgentCapability.ToolApproval);

    /// <summary>Read-only tool whitelist, or <see langword="null"/> when the profile is unrestricted.</summary>
    public IReadOnlyList<string>? ReadOnlyToolWhitelist =>
        Has(AgentCapability.ReadOnlyTools) ? ReadOnlyAgentTools : null;

    /// <summary>Resolves the capability set for a profile.</summary>
    /// <remarks>
    /// Profiles are written as subtractions from <see cref="AllCapabilities"/> so the baseline is
    /// stated once: a capability added to the enum is on for every profile until a profile
    /// explicitly drops it. That makes an accidental "capability leaked into Explore" visible here
    /// rather than being silently absent.
    /// </remarks>
    public static PipelineProfileBehavior For(PipelineProfile profile) => profile switch
    {
        // Full is the baseline: everything except the read-only restriction.
        PipelineProfile.Full => new(AllExcept(AgentCapability.ReadOnlyTools)),

        // Worker: inherits parent permissions; no state machine / 3-strike, and no LSP or shell
        // context (those are interactive-Main affordances). Working memory is withheld until private
        // sessions and artefact delivery are verified — a shared writable directory would be worse
        // than none. The agent's own checklist is independent per session, so it stays.
        PipelineProfile.Worker => new(AllExcept(
            AgentCapability.StateMachine,
            AgentCapability.TaskRecovery,
            AgentCapability.LspDiagnostics,
            AgentCapability.ShellEnvironment,
            AgentCapability.FileMemory,
            AgentCapability.ReadOnlyTools)),

        // TeamMember: fixed Team permission; MAF approval stays enabled — approval requests surface
        // as workflow external requests and TeamWorkflowRunner bridges them to the product events.
        // No CodeAct sandbox and no post-edit verification. Keeps design/task/LSP/shell context.
        // No working memory: members would share one writable directory across concurrent members.
        PipelineProfile.TeamMember => new(AllExcept(
            AgentCapability.StateMachine,
            AgentCapability.TaskRecovery,
            AgentCapability.CodeAct,
            AgentCapability.Verification,
            AgentCapability.FileMemory,
            AgentCapability.ReadOnlyTools)),

        // Read-only agents: no post-edit verification, contracts or 3-strike recovery, plus the tool
        // whitelist. No working memory either — a read-only agent must not be handed a write surface,
        // even one confined to its own scratch directory. The checklist is withheld for the same
        // reason: `todos_*` are write tools, and the read-only whitelist would reject them anyway,
        // leaving the agent with a prompt that advertises tools it cannot call.
        PipelineProfile.Explore or PipelineProfile.Plan => new(AllExcept(
            AgentCapability.StateMachine,
            AgentCapability.TaskRecovery,
            AgentCapability.LspDiagnostics,
            AgentCapability.ShellEnvironment,
            AgentCapability.BehaviorContracts,
            AgentCapability.Verification,
            AgentCapability.FileMemory,
            AgentCapability.Todo)),

        _ => throw new ArgumentOutOfRangeException(nameof(profile), profile, null),
    };

    /// <summary>Returns the full capability set with the given capabilities removed.</summary>
    private static IReadOnlySet<AgentCapability> AllExcept(params AgentCapability[] excluded)
    {
        var set = new HashSet<AgentCapability>(AllCapabilities);
        foreach (var capability in excluded)
            set.Remove(capability);
        return set;
    }

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
