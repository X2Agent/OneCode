namespace OneCode.Core.Agent;

/// <summary>
/// Names an optional agent capability that can be composed into a pipeline.
/// </summary>
/// <remarks>
/// <para>
/// This is the single source of truth for "what an agent profile turns on". Before it existed, the
/// same capability was expressed in three different shapes depending on where you looked:
/// positional booleans in <c>PipelineProfileBehavior.For</c>, a profile-to-bool switch in
/// <c>SharedContextProviderBuilder.ApplyProfileDefaults</c>, and <c>if</c> guards in
/// <c>BuildCommon</c>. Answering "does a TeamMember get LSP diagnostics?" meant reading all three.
/// </para>
/// <para>
/// Capabilities are resolved <b>statically at build time</b> from a per-profile set. There is no
/// runtime discovery, no reflection, and no DI scanning — this mirrors how MAF itself composes
/// pipelines (explicit, ordered registration; see ADR 0007 §1).
/// </para>
/// <para>
/// Naming follows the capability, not the implementing type, so a provider can be swapped without
/// renaming the capability.
/// </para>
/// </remarks>
public enum AgentCapability
{
    /// <summary>Skill discovery / progressive loading (<c>SKILL.md</c>, bundled skills, MCP skills).</summary>
    Skills,

    /// <summary>On-demand memory recall via the <c>search_memories</c> tool over <c>MEMORY.md</c>.</summary>
    MemorySearch,

    /// <summary>Design/document context from the session (tracked design docs and decisions).</summary>
    DesignContext,

    /// <summary>LSP diagnostics injected so the model sees compile errors without running a build.</summary>
    LspDiagnostics,

    /// <summary>Task list context (task dependency/status awareness).</summary>
    TaskContext,

    /// <summary>Shell environment probing for the foreground conversation (cwd, available tooling).</summary>
    ShellEnvironment,

    /// <summary>CodeAct (<c>execute_code</c>) sandbox tool surface.</summary>
    CodeAct,

    /// <summary>Failure-tracking state machine that gates tool use after repeated failures.</summary>
    StateMachine,

    /// <summary>Three-strike recovery guidance emitted by the state machine.</summary>
    TaskRecovery,

    /// <summary>Post-edit behavior contracts (e.g. mandatory file-edit validation).</summary>
    BehaviorContracts,

    /// <summary>Post-edit verification (build / type-check) feeding errors back to the model.</summary>
    Verification,

    /// <summary>MAF tool-approval flow (<c>ToolApprovalAgent</c> + auto-approval rules).</summary>
    ToolApproval,

    /// <summary>Tool-result size budget (character truncation of oversized results).</summary>
    ToolResultBudget,

    /// <summary>Read-only tool whitelist restricting mutating tools (Explore / Plan sub-agents).</summary>
    ReadOnlyTools,
}
